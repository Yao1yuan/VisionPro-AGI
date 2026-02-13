import argparse
import requests
import sys
import base64


def call_vpp_server(command, params={}, port=8000):
    """
    Sends a command to the VppDriver HTTP server and prints the result.
    """
    try:
        server_url = f"http://localhost:{port}"
        url = f"{server_url}/{command}"
        response = requests.get(url, params=params, timeout=10, proxies={"http": None, "https": None})
        response.raise_for_status()
        # To prevent encoding errors on Windows, write raw UTF-8 bytes directly to the stdout buffer.
        sys.stdout.buffer.write(response.content)
    except requests.exceptions.RequestException as e:
        print(f"[Error] Failed to connect or communicate with the VppDriver server.", file=sys.stderr)
        if e.response:
            print(f"  Status: {e.response.status_code}\n  Message: {e.response.text}", file=sys.stderr)
        else:
            print(f"  Message: {e}", file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print(f"[Error] An unexpected client error occurred: {e}", file=sys.stderr)
        sys.exit(1)

def main():
    parser = argparse.ArgumentParser(
        description="A lightweight HTTP client for the VppDriver server.",
        formatter_class=argparse.RawTextHelpFormatter
    )
    parser.add_argument("--port", type=int, default=8000, help="The port number for the VppDriver server.")
    subparsers = parser.add_subparsers(dest="action", required=True)

    # API endpoints are now updated to match your C# server.
    subparsers.add_parser("list_tools", help="List all tools from the server.")

    p_help = subparsers.add_parser("help", help="Get detailed properties of an object.")
    p_help.add_argument("path", help="Full property path (e.g., 'CogToolBlock1.RunParams').")

    p_get = subparsers.add_parser("get", help="Get a property value.")
    p_get.add_argument("tool", help="The base tool name.")
    p_get.add_argument("path", help="The property path relative to the tool.")

    p_set = subparsers.add_parser("set", help="Set a property value.")
    p_set.add_argument("tool")
    p_set.add_argument("path")
    p_set.add_argument("value")

    p_extract = subparsers.add_parser("extract", help="Extract a C# script.")
    p_extract.add_argument("tool")

    p_inject = subparsers.add_parser("inject", help="Inject a C# script.")
    p_inject.add_argument("tool")
    p_inject.add_argument("code", help="The full C# script content (will be Base64 encoded).")

    args = parser.parse_args()

    params = {}
    # Parameter names are now updated to match your C# server's query parameters.
    if args.action == "help":
        params = {"path": args.path}
    elif args.action == "get":
        params = {"tool": args.tool, "path": args.path}
    elif args.action == "set":
        params = {"tool": args.tool, "path": args.path, "value": args.value}
    elif args.action == "extract":
        params = {"tool": args.tool}
    elif args.action == "inject":
        encoded_code = base64.b64encode(args.code.encode('utf-8')).decode('ascii')
        params = {"tool": args.tool, "code": encoded_code}

    call_vpp_server(args.action, params, port=args.port)


if __name__ == "__main__":
    main()
