"""Small Streamable HTTP MCP client for the local Dalamud/EXDViewer servers."""
import argparse
import base64
import json
import os
from pathlib import Path
import sys
import urllib.error
import urllib.request


def local_bearer_token():
    """Read the current user token even when the caller predates its creation."""
    if sys.platform == "win32":
        import winreg
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as key:
                value, _ = winreg.QueryValueEx(key, "DALAMUD_MCP_BEARER_TOKEN")
            if value:
                return value
        except FileNotFoundError:
            pass
    return os.environ.get("DALAMUD_MCP_BEARER_TOKEN")


class Client:
    def __init__(self, url, timeout=30, token=None):
        self.url = url
        self.timeout = timeout
        self.headers = {
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
        }
        if token:
            from urllib.parse import urlparse
            endpoint = urlparse(url)
            if endpoint.scheme != "http" or endpoint.hostname not in ("127.0.0.1", "localhost", "::1"):
                raise ValueError("Local bearer token may only be sent to a loopback HTTP endpoint")
            self.headers["Authorization"] = "Bearer " + token
        self.sequence = 0
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def rpc(self, method, params=None, notification=False):
        body = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            body["params"] = params
        if not notification:
            self.sequence += 1
            body["id"] = self.sequence
        request = urllib.request.Request(
            self.url, data=json.dumps(body).encode("utf-8"),
            headers=self.headers, method="POST",
        )
        with self.opener.open(request, timeout=self.timeout) as response:
            session = response.headers.get("Mcp-Session-Id")
            if session:
                self.headers["Mcp-Session-Id"] = session
            if notification or response.status == 202:
                return None
            if "text/event-stream" in response.headers.get("Content-Type", ""):
                data = []
                for raw in response:
                    line = raw.decode("utf-8").rstrip("\r\n")
                    if line.startswith("data:"):
                        data.append(line[5:].lstrip(" "))
                    elif not line and data:
                        message = json.loads("\n".join(data))
                        data = []
                        if message.get("id") == body["id"]:
                            return self.unwrap(message)
                if data:
                    message = json.loads("\n".join(data))
                    if message.get("id") == body["id"]:
                        return self.unwrap(message)
                raise RuntimeError("SSE ended without the matching JSON-RPC response")
            return self.unwrap(json.load(response))

    @staticmethod
    def unwrap(message):
        if "error" in message:
            raise RuntimeError(json.dumps(message["error"], ensure_ascii=False))
        return message.get("result", {})

    def initialize(self):
        result = self.rpc("initialize", {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "codex-local-mcp", "version": "1.0.0"},
        })
        self.headers["MCP-Protocol-Version"] = result["protocolVersion"]
        self.rpc("notifications/initialized", notification=True)
        return result

    def close(self):
        if "Mcp-Session-Id" not in self.headers:
            return
        try:
            request = urllib.request.Request(self.url, headers=self.headers, method="DELETE")
            with self.opener.open(request, timeout=min(self.timeout, 5)):
                pass
        except (urllib.error.URLError, TimeoutError):
            pass  # Session termination is optional in MCP.


def save_images(result, directory):
    directory.mkdir(parents=True, exist_ok=True)
    for index, item in enumerate(result.get("content", [])):
        if item.get("type") == "image":
            extension = {"image/png": ".png", "image/jpeg": ".jpg", "image/webp": ".webp"}.get(item.get("mimeType"), ".bin")
            import uuid
            destination = directory / ("mcp-" + uuid.uuid4().hex + "-" + str(index) + extension)
            destination.write_bytes(base64.b64decode(item.pop("data"), validate=True))
            item["saved_path"] = str(destination.resolve())
    return result


def discover_instances():
    directory = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local")) / "DalamudMCP" / "instances"
    instances = []
    for file in list(directory.glob("*.json"))[:256]:
        try:
            if file.stat().st_size > 4096:
                continue
            entry = json.loads(file.read_text(encoding="utf-8-sig"))
            if not 1024 <= entry["port"] <= 65535 or not isinstance(entry["gameProcessID"], int):
                continue
            endpoint = "http://127.0.0.1:" + str(entry["port"]) + "/mcp"
            client = Client(endpoint, timeout=2, token=local_bearer_token())
            try:
                client.initialize()
                status = client.rpc("tools/call", {"name": "get_session_status", "arguments": {}})
                data = status.get("structuredContent", {}).get("data", {})
                if data.get("processId") != entry["gameProcessID"] or data.get("instanceId") != entry["instanceID"]:
                    continue
                instances.append({**entry, "endpoint": endpoint, "loggedIn": data.get("loggedIn"),
                                  "territoryID": data.get("territoryID")})
            finally:
                client.close()
        except (OSError, ValueError, KeyError, TypeError, RuntimeError):
            continue
    return sorted(instances, key=lambda entry: entry["port"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", choices=["dalamud", "exd"], default="dalamud")
    parser.add_argument("--url", help="Override the local MCP endpoint")
    parser.add_argument("--timeout", type=float, default=30)
    parser.add_argument("--pid", type=int, help="Select an exact game process from the live instance registry")
    parser.add_argument("--instance", help="Pin the plugin instance ID; stale instances are never silently replaced")
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("initialize")
    subparsers.add_parser("instances")
    listing = subparsers.add_parser("list")
    listing.add_argument("--filter", default="")
    listing.add_argument("--schemas", action="store_true")
    call = subparsers.add_parser("call")
    call.add_argument("name")
    arguments = call.add_mutually_exclusive_group()
    arguments.add_argument("--arguments", default=None)
    arguments.add_argument("--arguments-file", type=Path)
    call.add_argument("--image-dir", type=Path)
    args = parser.parse_args()
    if args.command == "instances":
        print(json.dumps({"instances": discover_instances()}, ensure_ascii=False, indent=2))
        return 0
    if args.url and (args.pid is not None or args.instance):
        parser.error("--url cannot be combined with --pid or --instance")
    selected = None
    if args.server == "dalamud" and not args.url:
        instances = discover_instances()
        matches = [entry for entry in instances if (args.pid is None or entry["gameProcessID"] == args.pid)
                   and (not args.instance or entry["instanceID"] == args.instance)]
        if len(matches) > 1:
            raise ValueError("Multiple game instances are running. Use instances, then select --pid or --instance explicitly.")
        if not matches and (args.pid is not None or args.instance):
            raise ValueError("The selected game instance is unavailable; no other instance was selected.")
        selected = matches[0] if matches else None
    if selected:
        args.url = selected["endpoint"]
    endpoint = args.url or {"dalamud": "http://127.0.0.1:61016/mcp", "exd": "http://127.0.0.1:3001/mcp"}[args.server]
    client = Client(endpoint, args.timeout, local_bearer_token() if args.server == "dalamud" else None)
    try:
        result = client.initialize()
        if selected:
            check = client.rpc("tools/call", {"name": "get_session_status", "arguments": {}})
            data = check.get("structuredContent", {}).get("data", {})
            if data.get("processId") != selected["gameProcessID"] or data.get("instanceId") != selected["instanceID"]:
                raise RuntimeError("The endpoint changed game instances. Request cancelled.")
        if args.command == "list":
            tools = []
            params = {}
            while True:
                page = client.rpc("tools/list", params)
                tools.extend(page.get("tools", []))
                if not page.get("nextCursor"):
                    break
                params = {"cursor": page["nextCursor"]}
            matching = [t for t in tools if args.filter.casefold() in (t["name"] + " " + t.get("description", "") + " " + t.get("title", "")).casefold()]
            result = {"endpoint": endpoint, "total": len(tools), "tools": matching if args.schemas else [{"name": t["name"], "description": t.get("description", "")} for t in matching]}
        elif args.command == "call":
            values = json.loads(args.arguments_file.read_text(encoding="utf-8-sig") if args.arguments_file else args.arguments or "{}")
            if not isinstance(values, dict):
                raise ValueError("Tool arguments must be a JSON object")
            result = client.rpc("tools/call", {"name": args.name, "arguments": values})
            if args.image_dir:
                result = save_images(result, args.image_dir)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 1 if result.get("isError") else 0
    finally:
        client.close()


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    try:
        sys.exit(main())
    except (OSError, ValueError, RuntimeError) as error:
        print(json.dumps({"error": str(error)}, ensure_ascii=False), file=sys.stderr)
        sys.exit(1)
