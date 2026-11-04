"""Small standard-library client for the SAP RPA schedule administration API."""

from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass
from ipaddress import ip_address
from typing import Any, Iterable
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlsplit, urlunsplit
from urllib.request import Request, urlopen


DEFAULT_API_BASE = "http://127.0.0.1:8080"
API_BASE_ENV = "SAP_RPA_SCHEDULE_API_BASE"
_PERSONNEL_RE = re.compile(r"\d{5,12}")
_SAFE_PARAM_KEYS = (
    "plants",
    "plant",
    "plantCodes",
    "factoryCodes",
    "businessAreas",
    "businessArea",
    "factoryGroup",
    "rangeKind",
    "runStrategy",
    "dateRule",
    "period",
    "weekEnd",
    "startDate",
    "endDate",
    "year",
    "week",
    "tcode",
    "script",
    "timeoutSeconds",
)


class ScheduleApiError(RuntimeError):
    """An API or transport error safe to show to an administrator."""


def normalize_api_base(value: str | None) -> str:
    raw = (value or "").strip() or os.environ.get(API_BASE_ENV, "").strip() or DEFAULT_API_BASE
    parsed = urlsplit(raw)
    if parsed.scheme not in {"http", "https"} or not parsed.hostname:
        raise ValueError("API 地址必须是有效的 http:// 或 https:// 地址")
    hostname = parsed.hostname.casefold()
    is_loopback = hostname == "localhost"
    if not is_loopback:
        try:
            is_loopback = ip_address(hostname).is_loopback
        except ValueError:
            is_loopback = False
    if not is_loopback:
        raise ValueError("管理工具只能连接本机回环地址（127.0.0.1、localhost 或 ::1）")
    if parsed.username or parsed.password:
        raise ValueError("API 地址不能包含用户名或密码")
    if parsed.query or parsed.fragment:
        raise ValueError("API 地址不能包含查询参数或片段")
    path = parsed.path.rstrip("/")
    return urlunsplit((parsed.scheme, parsed.netloc, path, "", ""))


def _first_text(item: dict[str, Any], *keys: str) -> str:
    for key in keys:
        value = item.get(key)
        if value is not None and str(value).strip():
            return str(value).strip()
    return ""


def _string_list(value: Any) -> tuple[str, ...]:
    if isinstance(value, list):
        return tuple(str(part).strip() for part in value if str(part).strip())
    if isinstance(value, str):
        return tuple(part.strip() for part in value.split(",") if part.strip())
    return ()


def _params_dict(item: dict[str, Any]) -> dict[str, Any]:
    raw = item.get("params")
    if isinstance(raw, dict):
        return raw
    raw_json = item.get("paramsJson") or item.get("params_json")
    if not isinstance(raw_json, str) or not raw_json.strip():
        return {}
    try:
        candidate = json.loads(raw_json)
    except json.JSONDecodeError:
        return {}
    return candidate if isinstance(candidate, dict) else {}


def _extract_personnel_numbers(item: dict[str, Any]) -> tuple[str, ...]:
    params = _params_dict(item)
    params_by_key = {str(key).casefold(): value for key, value in params.items()}

    # A schedule has one authoritative webpage employee number. Prefer the API's
    # canonical top-level value, then the canonical params value, then old aliases.
    priority_groups = (
        (item.get("operatorId"), item.get("operator_id")),
        (params_by_key.get("operatorid"), params_by_key.get("operator_id")),
        (item.get("personnelNumber"), item.get("personnel_number")),
        (params_by_key.get("personnelnumber"), params_by_key.get("personnel_number")),
        (item.get("pernr"), params_by_key.get("pernr")),
        (item.get("employeeId"), item.get("employee_id")),
        (params_by_key.get("employeeid"), params_by_key.get("employee_id")),
    )
    for candidates in priority_groups:
        for candidate in candidates:
            value = str(candidate or "").strip()
            if _PERSONNEL_RE.fullmatch(value):
                return (value,)

    # Legacy schedules did not persist operatorId. Only an entirely numeric
    # DingTalk target is safe as a fallback; converted IDs such as ddid-example are not.
    notify = item.get("notify")
    notify_target = str(notify.get("target") or "") if isinstance(notify, dict) else ""
    notify_target = notify_target or str(item.get("notifyTarget") or "")
    legacy_value = notify_target.strip()
    if legacy_value.casefold().startswith("dingtalk:"):
        legacy_value = legacy_value.split(":", 1)[1].strip()
    if _PERSONNEL_RE.fullmatch(legacy_value):
        return (legacy_value,)
    return ()


def _safe_params(item: dict[str, Any]) -> dict[str, Any]:
    raw = _params_dict(item)
    return {key: raw[key] for key in _SAFE_PARAM_KEYS if key in raw and raw[key] not in (None, "", [], {})}


@dataclass(frozen=True)
class ScheduleTask:
    task_id: str
    name: str
    tcode: str
    created_by: str
    updated_by: str
    personnel_numbers: tuple[str, ...]
    plants: tuple[str, ...]
    business_areas: tuple[str, ...]
    factory_group: str
    frequency: str
    weekday_label: str
    run_time: str
    enabled: bool
    next_run_at: str
    created_at: str
    updated_at: str
    safe_params: dict[str, Any]

    @property
    def owner(self) -> str:
        return self.updated_by or self.created_by

    @property
    def personnel_text(self) -> str:
        return ", ".join(self.personnel_numbers)

    @classmethod
    def from_api(cls, item: dict[str, Any]) -> "ScheduleTask":
        return cls(
            task_id=_first_text(item, "id", "taskId", "scheduleId"),
            name=_first_text(item, "name", "taskName", "title"),
            tcode=_first_text(item, "tcode", "tCode", "transactionCode", "code").upper(),
            created_by=_first_text(item, "createdBy", "created_by"),
            updated_by=_first_text(item, "updatedBy", "updated_by"),
            personnel_numbers=_extract_personnel_numbers(item),
            plants=_string_list(item.get("plants")),
            business_areas=_string_list(item.get("businessAreas") or item.get("business_areas")),
            factory_group=_first_text(item, "factoryGroup", "defaultBusinessScope", "plantGroupId"),
            frequency=_first_text(item, "frequency", "scheduleType"),
            weekday_label=_first_text(item, "weekdayLabel", "weekday", "dayOfWeek"),
            run_time=_first_text(item, "time", "runTime", "execTime"),
            enabled=bool(item.get("enabled", str(item.get("status", "")).lower() == "enabled")),
            next_run_at=_first_text(item, "nextRunAt", "nextExecutionTime", "next"),
            created_at=_first_text(item, "createdAt", "created_at"),
            updated_at=_first_text(item, "updatedAt", "updated_at"),
            safe_params=_safe_params(item),
        )

    def detail_text(self) -> str:
        fields = [
            ("任务 ID", self.task_id),
            ("任务名称", self.name),
            ("事务码", self.tcode),
            ("状态", "启用" if self.enabled else "停用"),
            ("创建人", self.created_by or "-"),
            ("更新人", self.updated_by or "-"),
            ("人员编号", self.personnel_text or "-"),
            ("组织", self.factory_group or "-"),
            ("工厂", ", ".join(self.plants) or "-"),
            ("业务范围", ", ".join(self.business_areas) or "-"),
            ("频率", " ".join(part for part in (self.frequency, self.weekday_label) if part) or "-"),
            ("执行时间", self.run_time or "-"),
            ("下次执行", self.next_run_at or "-"),
            ("创建时间", self.created_at or "-"),
            ("更新时间", self.updated_at or "-"),
        ]
        lines = [f"{label}：{value}" for label, value in fields]
        if self.safe_params:
            lines.append("")
            lines.append("执行参数（安全字段）：")
            for key, value in self.safe_params.items():
                display = ", ".join(str(part) for part in value) if isinstance(value, list) else str(value)
                lines.append(f"  {key}: {display}")
        return "\n".join(lines)


@dataclass(frozen=True)
class ScheduleFilters:
    keyword: str = ""
    owner: str = ""
    personnel_number: str = ""
    task_id: str = ""
    tcode: str = ""


def filter_tasks(tasks: Iterable[ScheduleTask], filters: ScheduleFilters) -> list[ScheduleTask]:
    keyword = filters.keyword.strip().casefold()
    owner = filters.owner.strip().casefold()
    personnel = filters.personnel_number.strip().casefold()
    task_id = filters.task_id.strip().casefold()
    tcode = filters.tcode.strip().casefold()
    result: list[ScheduleTask] = []
    for task in tasks:
        owner_text = f"{task.created_by} {task.updated_by}".casefold()
        personnel_values = {value.casefold() for value in task.personnel_numbers}
        personnel_text = task.personnel_text.casefold()
        haystack = " ".join(
            (task.task_id, task.name, task.tcode, owner_text, personnel_text, task.factory_group)
        ).casefold()
        if keyword and keyword not in haystack:
            continue
        if owner and owner not in owner_text:
            continue
        if personnel and personnel not in personnel_values:
            continue
        if task_id and task_id not in task.task_id.casefold():
            continue
        if tcode and tcode not in task.tcode.casefold():
            continue
        result.append(task)
    return result


def parse_schedule_list(payload: Any) -> list[ScheduleTask]:
    if isinstance(payload, list):
        raw_items = payload
    elif isinstance(payload, dict):
        raw_items = payload.get("scheduleTasks")
        if not isinstance(raw_items, list):
            raw_items = payload.get("schedules")
    else:
        raw_items = None
    if not isinstance(raw_items, list):
        raise ScheduleApiError("API 返回格式错误：未找到定时任务列表")
    tasks = [ScheduleTask.from_api(item) for item in raw_items if isinstance(item, dict)]
    return [task for task in tasks if task.task_id]


class ScheduleApiClient:
    def __init__(self, api_base: str | None = None, timeout: float = 15.0):
        self.api_base = normalize_api_base(api_base)
        self.timeout = timeout

    def list_schedules(self) -> list[ScheduleTask]:
        # Deliberately no owner query parameter: this is an administrator view.
        payload = self._request_json("GET", "/api/schedules")
        return parse_schedule_list(payload)

    def delete_schedule(self, task_id: str) -> int:
        clean_id = task_id.strip()
        if not clean_id:
            raise ValueError("任务 ID 不能为空")
        payload = self._request_json("DELETE", "/api/schedules/" + quote(clean_id, safe=""))
        if not isinstance(payload, dict) or payload.get("deleted") is not True:
            raise ScheduleApiError(f"任务 {clean_id} 的删除响应无效")
        try:
            return max(0, int(payload.get("removedQueuedRuns", 0)))
        except (TypeError, ValueError):
            return 0

    def _request_json(self, method: str, path: str) -> Any:
        request = Request(
            self.api_base + path,
            method=method,
            headers={"Accept": "application/json", "User-Agent": "SAP-RPA-Schedule-Admin/1.0"},
        )
        try:
            with urlopen(request, timeout=self.timeout) as response:
                data = response.read().decode("utf-8-sig")
        except HTTPError as exc:
            message = f"HTTP {exc.code}"
            try:
                body = json.loads(exc.read().decode("utf-8-sig"))
                if isinstance(body, dict) and body.get("error"):
                    message += f"：{body['error']}"
            except (UnicodeDecodeError, json.JSONDecodeError):
                pass
            raise ScheduleApiError(message) from None
        except (URLError, TimeoutError, OSError) as exc:
            reason = getattr(exc, "reason", exc)
            raise ScheduleApiError(f"无法连接本地 API：{reason}") from None
        try:
            return json.loads(data)
        except json.JSONDecodeError:
            raise ScheduleApiError("API 返回的不是有效 JSON") from None
