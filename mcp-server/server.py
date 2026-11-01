"""开发调试入口。

Official MCP SDK 不提供 FastMCP 那样的 `mcp dev` 集成界面，
Inspector 请改用独立客户端连接：

    # 方式一：Inspector 直接以 stdio 拉起本服务
    npx @modelcontextprotocol/inspector uv run mcp-server

    # 方式二：先启动 HTTP 端点，再在 Inspector 中选择 Streamable HTTP 连接
    uv run mcp-server --transport http --host 127.0.0.1 --port 8000
    # 端点：http://127.0.0.1:8000/mcp

普通启动见 mcp_server.__init__ 的 main。
"""

from mcp_server import main, server

if __name__ == "__main__":
    main()
