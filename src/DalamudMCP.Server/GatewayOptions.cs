namespace DalamudMCP.Server;

internal sealed record GatewayOptions(string Pipe, int Port, int? ParentID, bool Stdio)
{
    public static GatewayOptions Parse(string[] arguments)
    {
        string? pipe = null;
        var port = 61016;
        int? parentID = null;
        var stdio = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--pipe" when ++index < arguments.Length:
                    pipe = arguments[index];
                    break;
                case "--port" when ++index < arguments.Length:
                    port = int.Parse(arguments[index]);
                    break;
                case "--parent" when ++index < arguments.Length:
                    parentID = int.Parse(arguments[index]);
                    break;
                case "--stdio":
                    stdio = true;
                    break;
                default:
                    throw new ArgumentException("参数必须为 --pipe NAME --port PORT [--parent PID] [--stdio]。");
            }
        }

        if (string.IsNullOrWhiteSpace(pipe) || pipe.Length > 200 || port is < 1024 or > 65535 || parentID is <= 0)
        {
            throw new ArgumentException("必须提供有效的管道名、端口及父进程 ID。");
        }

        return new(pipe, port, parentID, stdio);
    }
}
