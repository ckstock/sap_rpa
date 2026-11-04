from __future__ import annotations

import json
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from schedule_api import (
    DEFAULT_API_BASE,
    ScheduleApiClient,
    ScheduleFilters,
    ScheduleTask,
    filter_tasks,
    normalize_api_base,
    parse_schedule_list,
)


SAMPLE = {
    "version": 2,
    "scheduleTasks": [
        {
            "id": "SCH-019",
            "name": "ZFI072A - 采购价月表",
            "tcode": "ZFI072A",
            "businessAreas": ["1440", "1180"],
            "createdBy": "Test User A/测试用户甲",
            "updatedBy": "Test User B/测试用户乙",
            "notify": {"target": "dingtalk:test-ddid-059", "secret": "must-not-display"},
            "paramsJson": json.dumps(
                {
                    "operatorId": "90000001",
                    "businessAreas": "1440,1180",
                    "period": "2026.10.01",
                    "password": "must-not-display",
                    "appSecret": "must-not-display",
                }
            ),
            "enabled": True,
        },
        {
            "id": "SCH-020",
            "name": "ZFI019NL - 周损益",
            "tcode": "ZFI019NL",
            "created_by": "Test Former User/测试离职用户 90000004",
            "updated_by": "",
            "notify": {"target": "dingtalk:90000004"},
            "enabled": False,
        },
    ],
}


class ScheduleParsingTests(unittest.TestCase):
    def test_default_api_base_is_production_local_port(self) -> None:
        self.assertEqual(DEFAULT_API_BASE, "http://127.0.0.1:8080")

    def test_normalize_api_base_rejects_embedded_credentials(self) -> None:
        with self.assertRaises(ValueError):
            normalize_api_base("http://user:password@127.0.0.1:8080")

    def test_normalize_api_base_rejects_remote_hosts(self) -> None:
        with self.assertRaises(ValueError):
            normalize_api_base("http://10.0.2.120:8080")

    def test_parses_primary_schedule_tasks_shape(self) -> None:
        tasks = parse_schedule_list(SAMPLE)
        self.assertEqual([task.task_id for task in tasks], ["SCH-019", "SCH-020"])
        self.assertEqual(tasks[0].personnel_numbers, ("90000001",))
        self.assertEqual(tasks[0].business_areas, ("1440", "1180"))

    def test_params_personnel_number_has_priority_over_transformed_dingtalk_id(self) -> None:
        payload = {
            "scheduleTasks": [
                {
                    "id": "SCH-PERSONNEL",
                    "notify": {"target": "dingtalk:test-ddid-059"},
                    "params": {"pernr": "90000002", "employeeId": "90000003", "appSecret": "hidden"},
                }
            ]
        }
        task = parse_schedule_list(payload)[0]
        self.assertEqual(task.personnel_numbers, ("90000002",))
        self.assertEqual(
            [item.task_id for item in filter_tasks([task], ScheduleFilters(personnel_number="90000002"))],
            ["SCH-PERSONNEL"],
        )
        self.assertNotIn("hidden", task.detail_text())

    def test_canonical_operator_id_wins_over_stale_alias(self) -> None:
        task = parse_schedule_list(
            {
                "scheduleTasks": [
                    {
                        "id": "SCH-TRANSFERRED",
                        "operatorId": "90000001",
                        "params": {"operatorId": "90000001", "pernr": "90000002"},
                    }
                ]
            }
        )[0]
        self.assertEqual(task.personnel_numbers, ("90000001",))
        self.assertEqual(filter_tasks([task], ScheduleFilters(personnel_number="90000002")), [])

    def test_personnel_filter_is_exact(self) -> None:
        task = parse_schedule_list(
            {
                "scheduleTasks": [
                    {
                        "id": "SCH-EXACT",
                        "operatorId": "90000002",
                        "createdBy": "Owner 1004",
                        "notify": {"target": "dingtalk:test-ddid-059"},
                    }
                ]
            }
        )[0]
        self.assertEqual(task.personnel_numbers, ("90000002",))
        self.assertEqual(filter_tasks([task], ScheduleFilters(personnel_number="1004")), [])
        self.assertEqual(
            [item.task_id for item in filter_tasks([task], ScheduleFilters(personnel_number="90000002"))],
            ["SCH-EXACT"],
        )

    def test_transformed_dingtalk_id_is_not_treated_as_personnel_number(self) -> None:
        task = parse_schedule_list(
            {"scheduleTasks": [{"id": "SCH-DDID", "notify": {"target": "dingtalk:test-ddid-059"}}]}
        )[0]
        self.assertEqual(task.personnel_numbers, ())

    def test_safe_detail_excludes_secrets_and_unknown_fields(self) -> None:
        task = parse_schedule_list(SAMPLE)[0]
        detail = task.detail_text()
        self.assertIn("90000001", detail)
        self.assertIn("businessAreas", detail)
        self.assertNotIn("must-not-display", detail)
        self.assertNotIn("password", detail)
        self.assertNotIn("appSecret", detail)

    def test_filters_each_required_field(self) -> None:
        tasks = parse_schedule_list(SAMPLE)
        cases = (
            (ScheduleFilters(owner="用户甲"), ["SCH-019"]),
            (ScheduleFilters(owner="Test User B"), ["SCH-019"]),
            (ScheduleFilters(personnel_number="90000001"), ["SCH-019"]),
            (ScheduleFilters(personnel_number="90000004"), ["SCH-020"]),
            (ScheduleFilters(task_id="019"), ["SCH-019"]),
            (ScheduleFilters(tcode="zfi019nl"), ["SCH-020"]),
            (ScheduleFilters(keyword="采购价"), ["SCH-019"]),
        )
        for filters, expected in cases:
            with self.subTest(filters=filters):
                self.assertEqual([task.task_id for task in filter_tasks(tasks, filters)], expected)


class _Handler(BaseHTTPRequestHandler):
    seen_paths: list[str] = []

    def do_GET(self) -> None:  # noqa: N802 - stdlib handler API
        type(self).seen_paths.append(self.path)
        body = json.dumps(SAMPLE, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_DELETE(self) -> None:  # noqa: N802 - stdlib handler API
        type(self).seen_paths.append(self.path)
        body = json.dumps({"ok": True, "deleted": True, "removedQueuedRuns": 3}).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, _format: str, *_args: object) -> None:
        pass


class ScheduleApiClientTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        _Handler.seen_paths = []
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), _Handler)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.base = f"http://127.0.0.1:{cls.server.server_port}"

    @classmethod
    def tearDownClass(cls) -> None:
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(timeout=2)

    def setUp(self) -> None:
        _Handler.seen_paths.clear()

    def test_list_has_no_owner_filter(self) -> None:
        tasks = ScheduleApiClient(self.base).list_schedules()
        self.assertEqual(len(tasks), 2)
        self.assertEqual(_Handler.seen_paths, ["/api/schedules"])

    def test_delete_surfaces_removed_queued_runs(self) -> None:
        removed = ScheduleApiClient(self.base).delete_schedule("SCH-019")
        self.assertEqual(removed, 3)
        self.assertEqual(_Handler.seen_paths, ["/api/schedules/SCH-019"])


if __name__ == "__main__":
    unittest.main()
