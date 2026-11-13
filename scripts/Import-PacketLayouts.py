import argparse
import base64
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import tarfile
import urllib.request
import zipfile

import yaml

ROOT = Path(__file__).resolve().parents[1]
LOCK = json.loads((ROOT / "scripts/packet-sources.lock.json").read_text(encoding="utf-8"))


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        raise ValueError("Source redirects are not accepted")


def download(url, maximum):
    if not url.startswith("https://"):
        raise ValueError("HTTPS source required")
    request = urllib.request.Request(url, headers={"User-Agent": "DalamudMCP-layout-import"})
    with urllib.request.build_opener(NoRedirect).open(request, timeout=25) as reply:
        data = reply.read(maximum + 1)
    if len(data) > maximum:
        raise ValueError("Source exceeds byte limit")
    return data


def sha(data):
    return hashlib.sha256(data).hexdigest()


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


def source_digest(data, prefix):
    files = {}
    total = 0
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        if len(archive.infolist()) > 4096:
            raise ValueError("Too many archive entries")
        for entry in archive.infolist():
            if entry.is_dir() or "/" not in entry.filename:
                continue
            relative = entry.filename.split("/", 1)[1]
            if not relative.startswith(prefix):
                continue
            name = relative[len(prefix):]
            if not name.isascii() or ".." in PurePosixPath(name).parts or "\\" in name or name.startswith("/"):
                raise ValueError("Invalid structure path")
            if name in files or len(files) >= 512 or entry.file_size > 1024 * 1024:
                raise ValueError("Invalid structure archive bounds")
            with archive.open(entry) as stream:
                content = stream.read(1024 * 1024 + 1)
            total += len(content)
            if len(content) > 1024 * 1024 or total > 8 * 1024 * 1024:
                raise ValueError("Structure data exceeds limit")
            files[name] = sha(content)
    if not files:
        raise ValueError("Structure directory missing")
    return sha("".join(name + "\0" + digest + "\n" for name, digest in sorted(files.items())).encode()), len(files)


def import_packages(work):
    for package in LOCK["packages"]:
        data = download(package["url"], 4 * 1024 * 1024)
        if "sha512-" + base64.b64encode(hashlib.sha512(data).digest()).decode() != package["integrity"]:
            raise ValueError("Package integrity changed: " + package["name"])
        destination = work / "node_modules" / package["name"]
        total = 0
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
            for entry in archive:
                parts = PurePosixPath(entry.name).parts
                if not parts or parts[0] != "package" or ".." in parts or "\\" in entry.name:
                    raise ValueError("Invalid package path")
                if entry.isdir():
                    continue
                if not entry.isfile() or entry.size > 4 * 1024 * 1024:
                    raise ValueError("Invalid package entry")
                total += entry.size
                if total > 8 * 1024 * 1024:
                    raise ValueError("Package content exceeds limit")
                target = destination.joinpath(*parts[1:])
                target.parent.mkdir(parents=True, exist_ok=True)
                source = archive.extractfile(entry)
                if source is None:
                    raise ValueError("Package file missing")
                target.write_bytes(source.read())
        manifest = json.loads((destination / "package.json").read_text(encoding="utf-8"))
        if manifest["name"] != package["name"] or manifest["version"] != package["version"]:
            raise ValueError("Package identity mismatch")


def normalize(metadata):
    structures = {item["id"]: item for item in metadata["structures"]}
    if len(structures) != len(metadata["structures"]):
        raise ValueError("Duplicate structure identity")
    primitive = {"byte": "u8", "bigint": "i64", "biguint": "u64", "float": "f32", "double": "f64",
                 "string": "utf8", "bytes": "bytes", "bitset": "bitset"}
    numbered = {0: "string", 1: "int", 2: "uint", 3: "bigint", 4: "biguint", 5: "float", 6: "double", 7: "byte", 8: "bytes"}

    def fields_for(identity, ancestry=()):
        if identity in ancestry or len(ancestry) >= 12:
            raise ValueError("Cyclic or excessively nested definition")
        structure = structures[identity]
        enums = {item["name"]: item["values"] for item in structure["enums"]}
        output = []
        for field in structure["fields"]:
            row = {"name": field["name"], "offset": field["offset"]}
            kind = field["type"]
            length = field.get("length")
            if kind in ("array", "object"):
                child = field["child"]
                if isinstance(child, str):
                    width = structures[child]["size"]
                    row.update(type="object", length=width, fields=fields_for(child, (*ancestry, identity)))
                else:
                    width = child["byteLength"]
                    scalar = numbered[child["type"]]
                    row["type"] = primitive.get(scalar) or ("u" if scalar == "uint" else "i") + str(width * 8)
                    if scalar in ("bytes", "string", "bitset"):
                        row["length"] = width
                if not isinstance(width, int) or width <= 0:
                    raise ValueError("Invalid array stride")
                if kind == "array":
                    if length is None or length < width or length % width:
                        raise ValueError("Array length does not match its declaration")
                    row.update(count=length // width, stride=width)
            elif kind in ("int", "uint"):
                if length not in (1, 2, 4, 8):
                    raise ValueError("Unsupported integer width")
                row["type"] = ("u" if kind == "uint" else "i") + str(length * 8)
            else:
                row["type"] = primitive[kind]
                if kind in ("bytes", "string", "bitset"):
                    if length is None:
                        row["remaining"] = True
                    else:
                        row["length"] = length
            info = {"structure": identity}
            if "condition" in structure:
                info["structureCondition"] = structure["condition"]
            if "condition" in field:
                info["condition"] = field["condition"]
            format_spec = field.get("format", {})
            if format_spec:
                info["format"] = format_spec
            if "divisor" in format_spec:
                row["divisor"] = format_spec["divisor"]
            if "addend" in format_spec:
                row["addend"] = format_spec["addend"]
            enum_name = format_spec.get("enum")
            if enum_name:
                labels = {}
                for name, value in enums.get(enum_name, {}).items():
                    if isinstance(value, (int, float)) and not isinstance(value, bool):
                        labels.setdefault(str(value), []).append(name)
                if labels:
                    row["enum_labels"] = labels
                else:
                    info["enumUnavailable"] = enum_name
            row["metadata"] = info
            if row.get("count", 1) > 1024 or row.get("length", 0) > 65536:
                raise ValueError("Definition exceeds supported field bounds")
            output.append(row)
        return output

    return structures, fields_for


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--work-dir", required=True)
    parser.add_argument("--node", default="node")
    args = parser.parse_args()
    work = Path(args.work_dir).resolve()
    if work.exists():
        raise ValueError("Use a new, empty work directory")
    work.mkdir(parents=True)
    import_packages(work)
    shutil.copyfile(ROOT / "scripts/Export-PacketMetadata.mjs", work / "export.mjs")
    result = subprocess.run([args.node, str(work / "export.mjs")], cwd=work, check=True, capture_output=True,
                            timeout=30, creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    metadata = json.loads(result.stdout)
    if len(metadata["packets"]) != LOCK["expected"]["packets"] or len(metadata["structures"]) != LOCK["expected"]["structures"]:
        raise ValueError("Published definition counts changed")
    source = LOCK["structureSource"]
    archive = download("https://codeload.github.com/" + source["repository"] + "/zip/" + source["revision"], 4 * 1024 * 1024)
    if sha(archive) != source["archiveSha256"] or source_digest(archive, source["path"]) != (source["contentSha256"], source["fileCount"]):
        raise ValueError("Reference source changed")
    alias = LOCK["aliasSource"]
    base = "https://raw.githubusercontent.com/" + alias["repository"] + "/" + alias["revision"] + "/"
    catalog = download(base + alias["path"], 1024 * 1024)
    license_text = download(base + "LICENSE", 128 * 1024)
    if sha(catalog) != alias["sha256"] or sha(license_text) != alias["licenseSha256"]:
        raise ValueError("Alias source changed")
    aliases = []
    for category, group in yaml.safe_load(catalog).items():
        direction = {"server-to-client": "receive", "client-to-server": "send"}[group["direction"]]
        channel = {"ServerZoneIpc": "zone", "ClientZoneIpc": "zone", "ServerLobbyIpc": "lobby",
                   "ClientLobbyIpc": "lobby", "ServerChatIpc": "chat", "ClientChatIpc": "chat"}[category]
        for name, providers in group["packets"].items():
            aliases.append(dict(name=name, category=category, direction=direction, channel=channel, providers=providers))
    structures, fields_for = normalize(metadata)
    packets = []
    for packet in metadata["packets"]:
        matches = [item for item in aliases if item["name"] == packet["name"]]
        if len(matches) > 1:
            raise ValueError("Ambiguous packet category")
        catalog_item = matches[0] if matches else None
        fields = fields_for(packet["structure"])
        names = [packet["name"]]
        if catalog_item and catalog_item["providers"].get("FFXIVOpcodes"):
            names.append(catalog_item["providers"]["FFXIVOpcodes"])
        packets.append(dict(name=packet["name"], structure=packet["structure"], minimumBytes=structures[packet["structure"]]["size"],
                            direction=catalog_item["direction"] if catalog_item else "unknown", channel=catalog_item["channel"] if catalog_item else "unknown",
                            opcodeNames=sorted(set(names)), fields=fields,
                            sha256=sha(json.dumps(fields, ensure_ascii=False, separators=(",", ":")).encode())))
    output = {"formatVersion": 1, "provenance": LOCK, "packets": packets, "structures": metadata["structures"], "aliases": aliases}
    write_json(ROOT / "src/DalamudMCP.Plugin/Data/packet-layouts.json", output)
    print(json.dumps({"packets": len(packets), "structures": len(structures), "aliases": len(aliases),
                      "zoneMappings": sum(item["channel"] == "zone" for item in packets),
                      "snapshotSha256": sha((ROOT / "src/DalamudMCP.Plugin/Data/packet-layouts.json").read_bytes())}))


if __name__ == "__main__":
    main()
