"""用独立 Nginx 进程和合成上游验证发布片段，不读取或加载现有 Nginx 配置。

在有 Nginx 的 Linux 环境执行 python tests/nginx/device-api-proxy.test.py；
可通过 NGINX_BINARY 指定可执行文件。所有监听均为回环临时端口，不连接真实手机。
"""
import http.server
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request


class DeviceApiProxyTests(unittest.TestCase):
    """验证真实代理的路径、方法与凭据转发；后端实际认证另由 .NET HTTP 测试覆盖。"""

    @classmethod
    def setUpClass(cls):
        """启动合成上游与隔离代理；失败时同样只清理本次实例。"""
        binary = os.environ.get("NGINX_BINARY") or shutil.which("nginx")
        if not binary:
            raise unittest.SkipTest("需要 Nginx 可执行文件")
        cls.directory = tempfile.TemporaryDirectory(prefix="office-device-api-test-")
        cls.addClassCleanup(cls.directory.cleanup)
        root = Path(cls.directory.name)
        root.chmod(0o755)
        cls.records = []
        cls.delay = 0
        cls.upstream_status = 200

        class Backend(http.server.BaseHTTPRequestHandler):
            """只记录合成请求，不模拟或执行设备动作。"""

            def log_message(self, *_):
                """禁用默认 URL 日志；测试不输出请求头或密钥。"""

            def handle_request(self):
                """返回合成响应，提供认证透传与长轮询的可观察证据。"""
                body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
                record = {"method": self.command, "path": self.path, "body": body.decode(),
                          "device_id": self.headers.get("X-Device-Id"),
                          "device_key": self.headers.get("X-Device-Key"),
                          "authorization": self.headers.get("Authorization"),
                          "api_key": self.headers.get("X-Api-Key")}
                cls.records.append(record)
                time.sleep(cls.delay)
                status = cls.upstream_status
                if record["device_id"] != "office-phone" or record["device_key"] != "synthetic-device-key":
                    status = 401
                payload = json.dumps({"request_number": len(cls.records)}).encode()
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            do_POST = handle_request
            do_GET = handle_request

        cls.backend = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Backend)
        cls.addClassCleanup(cls.backend.server_close)
        cls.addClassCleanup(cls.backend.shutdown)
        cls.backend.daemon_threads = True
        threading.Thread(target=cls.backend.serve_forever, daemon=True).start()
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            cls.port = listener.getsockname()[1]
        template = Path(__file__).resolve().parents[2] / "deploy/nginx/office_mcp_device_api.locations.conf"
        fragment = template.read_text(encoding="utf-8").replace(
            "127.0.0.1:18101", "127.0.0.1:" + str(cls.backend.server_port))
        (root / "device.locations.conf").write_text(fragment, encoding="utf-8")
        config = root / "nginx.conf"
        config.write_text(f"""
worker_processes 1;
error_log "{root}/error.log" notice;
pid "{root}/nginx.pid";
events {{ worker_connections 64; }}
http {{
    access_log off;
    client_body_temp_path "{root}/body";
    proxy_temp_path "{root}/proxy";
    server {{
        listen 127.0.0.1:{cls.port};
        server_name localhost;
        include "{root}/device.locations.conf";
        # 代表原有 MCP 代理：未匹配设备前缀时仍进入原 location。
        location / {{ return 418; }}
    }}
}}
""", encoding="utf-8")
        cls.process = subprocess.Popen(
            [binary, "-p", str(root) + "/", "-c", str(config), "-g", "daemon off;"],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        cls.addClassCleanup(cls.stop_proxy)
        cls.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        deadline = time.monotonic() + 5
        while True:
            try:
                if cls.call("/office_mcp_device_api/", authenticated=False)[0] == 404:
                    break
            except OSError:
                pass
            if cls.process.poll() is not None or time.monotonic() >= deadline:
                raise RuntimeError("隔离 Nginx 未就绪：" + (root / "error.log").read_text())
            time.sleep(0.05)

    @classmethod
    def stop_proxy(cls):
        """只终止本测试启动的前台进程，避免影响已有服务。"""
        if cls.process.poll() is None:
            cls.process.terminate()
            try:
                cls.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                cls.process.kill()
                cls.process.wait(timeout=5)

    @classmethod
    def call(cls, path, method="GET", authenticated=True, extra=None):
        """请求本次回环端口，收集状态码并读取完响应以便正确关闭连接。"""
        headers = {"X-Device-Id": "office-phone", "X-Device-Key": "synthetic-device-key"} if authenticated else {}
        headers.update(extra or {})
        request = urllib.request.Request(f"http://127.0.0.1:{cls.port}" + path, method=method, headers=headers,
                                         data=b'{"wait_seconds":0}' if method == "POST" else None)
        try:
            response = cls.opener.open(request, timeout=35)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            return response.status, response.read()

    def setUp(self):
        """每项检查使用独立的可观察请求记录，不重启已就绪的代理。"""
        self.records.clear()
        type(self).delay = 0
        type(self).upstream_status = 200

    def test_six_routes_preserve_method_path_query_and_device_credentials(self):
        """六类请求只替换公网前缀，保留设备凭据和实际 HTTP 方法。"""
        routes = [
            ("POST", "commands/lease"),
            ("POST", "commands/" + "a" * 32 + "/report"),
            ("POST", "probes/" + "b" * 32 + "/ack"),
            ("POST", "attendance/executions"),
            ("POST", "attendance/automation/sync"),
            ("GET", "attendance/tasks/" + "c" * 32),
        ]
        for method, tail in routes:
            with self.subTest(route=tail):
                status, _ = self.call("/office_mcp_device_api/devices/office-phone/" + tail + "?sample=1", method)
                self.assertEqual(200, status)
                record = self.records[-1]
                self.assertEqual(method, record["method"])
                self.assertEqual("/api/devices/office-phone/" + tail + "?sample=1", record["path"])
                self.assertEqual("office-phone", record["device_id"])
                self.assertEqual("synthetic-device-key", record["device_key"])
                if method == "POST":
                    self.assertEqual('{"wait_seconds":0}', record["body"])
        self.assertEqual(6, len(self.records))

    def test_unknown_routes_never_reach_backend(self):
        """业务接口、文档、错误标识和未知设备路由不进入上游。"""
        for suffix in ["", "/", "/attendance", "/employees", "/openapi/v1.json", "/healthz",
                       "/api/devices/office-phone/commands/lease",
                       "/devices/office-phone/commands/arbitrary",
                       "/devices/office-phone/commands/not-a-task/report",
                       "/devices/office-phone/attendance/tasks/not-a-task"]:
            with self.subTest(path=suffix):
                self.assertEqual(404, self.call("/office_mcp_device_api" + suffix)[0])
        self.assertEqual([], self.records)

    def test_wrong_methods_never_reach_backend(self):
        """允许的路径也不能使用其他方法绕过边界。"""
        for method in ["GET", "PUT", "DELETE", "HEAD", "OPTIONS"]:
            self.assertEqual(405, self.call("/office_mcp_device_api/devices/office-phone/commands/lease", method)[0])
        for method in ["POST", "DELETE", "HEAD"]:
            self.assertEqual(405, self.call("/office_mcp_device_api/devices/office-phone/attendance/tasks/" + "a" * 32, method)[0])
        self.assertEqual([], self.records)

    def test_gateway_credentials_are_not_device_identity(self):
        """代理透传设备凭据、不注入身份，办公或网关凭据不能使合成上游接受请求。"""
        path = "/office_mcp_device_api/devices/office-phone/commands/lease"
        headers = {"Authorization": "Bearer test-token", "X-Api-Key": "test-office-key"}
        self.assertEqual(401, self.call(path, "POST", authenticated=False, extra=headers)[0])
        self.assertIsNone(self.records[-1]["device_key"])
        self.assertIsNone(self.records[-1]["authorization"])
        self.assertIsNone(self.records[-1]["api_key"])
        self.assertEqual(401, self.call(path, "POST", extra={"X-Device-Key": "wrong-device-key"})[0])
        self.assertEqual(200, self.call(path, "POST", extra=headers)[0])
        self.assertIsNone(self.records[-1]["authorization"])

    def test_existing_mcp_location_is_unchanged(self):
        """设备片段不拦截原 MCP 路由。"""
        self.assertEqual(418, self.call("/office_common_tools/mcp", "POST")[0])
        self.assertEqual([], self.records)

    def test_long_poll_can_exceed_twenty_five_seconds(self):
        """真实等待超过原领取上限，确认代理读取超时不会提前截断。"""
        type(self).delay = 25.2
        self.assertEqual(200, self.call("/office_mcp_device_api/devices/office-phone/commands/lease", "POST")[0])
        self.assertEqual(1, len(self.records))

    def test_upstream_error_is_not_retried_or_cached(self):
        """错误不由代理重复下发，后续请求仍抵达上游而非命中缓存。"""
        path = "/office_mcp_device_api/devices/office-phone/commands/lease"
        type(self).upstream_status = 503
        self.assertEqual(503, self.call(path, "POST")[0])
        self.assertEqual(1, len(self.records))
        type(self).upstream_status = 200
        self.assertEqual(200, self.call(path, "POST")[0])
        self.assertEqual(2, len(self.records))


if __name__ == "__main__":
    unittest.main(verbosity=2)
