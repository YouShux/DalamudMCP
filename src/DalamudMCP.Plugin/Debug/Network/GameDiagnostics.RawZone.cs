using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Application.Network;
using NativeFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace DalamudMCP.Plugin.Tools;

internal sealed partial class GameDiagnostics
{
    internal const string ZONE_BYTES_TOOL = "read_zone_packet_bytes";
    private const int CN_ZONE_SEND_RVA = 0x01D00A10;
    private const int CN_ZONE_DEQUEUE_RVA = 0x01D009F0;
    private const int IPC_HEADER_LENGTH = 16;
    private const int SEND_DATA_OFFSET = 32;
    private const int MAX_IPC_LENGTH = 1024 * 1024;
    private const int MAX_RECEIVE_IPC_LENGTH = 0x3BD8;
    private static readonly Lazy<Task<RawZoneBuild?>> RawZoneClientBuild = new(() => Task.Run(LoadRawZoneBuild));
    private Hook<ZoneDequeue>? rawReceiveTrace;

    internal static Task<RawZoneBuild?> VerifyRawZoneBuildAsync(CancellationToken token) => RawZoneClientBuild.Value.WaitAsync(token);

    private static RawZoneBuild? LoadRawZoneBuild()
    {
        if (!CurrentCnLayoutKnown) return null;
        try
        {
            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            if (module is null) return null;
            using var stream = File.Open(module.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(
                "52042B60631CF2C48851B3AAEE36E9EC4FEDD6D7F2C68F0E9A8C3BEFB0AF9F98", StringComparison.Ordinal)) return null;
            stream.Position = 0;
            using var executable = new PEReader(stream);
            return new(executable.GetSectionData(CN_ZONE_SEND_RVA).GetContent(0, 0x36).ToArray(),
                executable.GetSectionData(CN_ZONE_DEQUEUE_RVA).GetContent(0, 0x13).ToArray(),
                checked((int)Marshal.OffsetOf<NetworkModule>("ZoneClient")));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or
            System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or OverflowException)
        { return null; }
    }

    private RawZoneBindings FindRawZoneBindings(RawZoneBuild? build)
    {
        if (build is null || CurrentZoneClient(build.ZoneClientOffset) is not { } client || client == 0) return default;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        if (module is null) return default;
        var send = module.BaseAddress + CN_ZONE_SEND_RVA;
        var receive = module.BaseAddress + CN_ZONE_DEQUEUE_RVA;
        return new(ZoneClient.Addresses.SendPacket.Value == send && MatchesEntry(send, build.Send) ? send : 0,
            MatchesEntry(receive, build.Dequeue) ? receive : 0, client);

        bool MatchesEntry(nint address, ReadOnlySpan<byte> expected)
        {
            Span<byte> observed = stackalloc byte[expected.Length];
            var original = address;
            if (services.SignatureScanner is { IsCopy: true } scanner)
            {
                if (scanner.Module.BaseAddress != module.BaseAddress || scanner.SearchBase == 0) return false;
                original = checked(scanner.SearchBase + (address - module.BaseAddress));
            }
            return IsGameCode(address, module) && NativeMemoryReader.TryCopy(original, observed) && observed.SequenceEqual(expected);
        }
    }

    private static unsafe nint? CurrentZoneClient(int offset)
    {
        if (NativeFramework.Addresses.Instance.Value == 0) return null;
        var framework = NativeFramework.Instance();
        var proxy = framework != null && framework->IsNetworkModuleInitialized ? framework->NetworkModuleProxy : null;
        return proxy != null && proxy->NetworkModule != null &&
            NativeMemoryReader.TryRead((nint*)((byte*)proxy->NetworkModule + offset), out var client) ? client : null;
    }

    private static unsafe PacketSample? ReadSendPacket(nint packet, Span<byte> bytes)
    {
        if (!Offset(packet, SEND_DATA_OFFSET, out var data) ||
            !NativeMemoryReader.TryRead((uint*)packet, out var opcode) ||
            !NativeMemoryReader.TryRead((ulong*)(packet + 8), out var length) || length is < IPC_HEADER_LENGTH or > MAX_IPC_LENGTH)
            return null;
        var payloadLength = (int)length - IPC_HEADER_LENGTH;
        var take = Math.Min(payloadLength, bytes.Length);
        if (take != 0 && !NativeMemoryReader.TryCopy(data, bytes[..take])) return null;
        return new(opcode, (int)length, payloadLength, take, SEND_DATA_OFFSET, "zone_send_payload");
    }

    private static unsafe PacketSample? ReadReceivedPacket(nint result, Span<byte> bytes)
    {
        if (!Offset(result, 0x10, out var descriptorAddress) ||
            !NativeMemoryReader.TryRead((nint*)descriptorAddress, out var descriptor) ||
            !Offset(descriptor, 0x30, out var spanAddress) ||
            !NativeMemoryReader.TryRead((ReceiveSpan*)spanAddress, out var span) || span.Length is < IPC_HEADER_LENGTH or > MAX_RECEIVE_IPC_LENGTH ||
            !Offset(span.Data, 2, out var opcodeAddress) || !NativeMemoryReader.TryRead((ushort*)opcodeAddress, out var opcode))
            return null;
        var take = Math.Min((int)span.Length, bytes.Length);
        if (!NativeMemoryReader.TryCopy(span.Data, bytes[..take])) return null;
        return new(opcode, (int)span.Length, (int)span.Length, take, 0, "zone_receive_ipc");
    }

    private static bool Offset(nint source, int offset, out nint address)
    {
        address = 0;
        if (source == 0 || (nuint)source > nuint.MaxValue - (nuint)offset) return false;
        address = source + offset;
        return true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate byte ZoneDequeue(ZoneClient* client, nint result);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ReceiveSpan
    {
        public readonly ulong Length;
        public readonly nint Data;
    }

    internal sealed record RawZoneBuild(byte[] Send, byte[] Dequeue, int ZoneClientOffset);
    private readonly record struct RawZoneBindings(nint Send, nint Receive, nint Client);
    private readonly record struct PacketSample(uint Opcode, int IpcLength, int DataLength, int Count, int DataOffset, string Layout);
}
