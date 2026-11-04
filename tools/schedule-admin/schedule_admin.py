"""Tkinter administrator UI for centrally managing SAP RPA schedules."""

from __future__ import annotations

import argparse
import os
import queue
import sys
import threading

if getattr(sys, "frozen", False):
    # Python 3.14 ships Tcl/Tk libraries as ZIP archives; PyInstaller does not
    # discover those archives reliably, so point Tk at the bundled directories.
    _bundle_root = getattr(sys, "_MEIPASS", os.path.dirname(sys.executable))
    os.environ.setdefault("TCL_LIBRARY", os.path.join(_bundle_root, "lib", "tcl", "tcl_library"))
    os.environ.setdefault("TK_LIBRARY", os.path.join(_bundle_root, "lib", "tk", "tk_library"))

import tkinter as tk
from dataclasses import dataclass
from tkinter import messagebox, ttk
from typing import Callable

from schedule_api import (
    API_BASE_ENV,
    DEFAULT_API_BASE,
    ScheduleApiClient,
    ScheduleFilters,
    ScheduleTask,
    filter_tasks,
    normalize_api_base,
)


@dataclass
class WorkerResult:
    action: str
    value: object = None
    error: Exception | None = None


class ScheduleAdminApp(tk.Tk):
    def __init__(self, api_base: str | None = None):
        super().__init__()
        self.title("SAP RPA 定时任务集中管理")
        self.geometry("1280x760")
        self.minsize(1000, 640)

        self.tasks: list[ScheduleTask] = []
        self.visible_tasks: list[ScheduleTask] = []
        self.task_by_id: dict[str, ScheduleTask] = {}
        self.results: queue.Queue[WorkerResult] = queue.Queue()
        self.busy = False

        self.api_base_var = tk.StringVar(value=normalize_api_base(api_base))
        self.keyword_var = tk.StringVar()
        self.owner_var = tk.StringVar()
        self.personnel_var = tk.StringVar()
        self.task_id_var = tk.StringVar()
        self.tcode_var = tk.StringVar()
        self.summary_var = tk.StringVar(value="尚未加载")
        self.status_var = tk.StringVar(value="就绪")

        self._build_ui()
        self.after(100, self._poll_worker_results)
        self.after(150, self.refresh)

    def _build_ui(self) -> None:
        root = ttk.Frame(self, padding=12)
        root.pack(fill=tk.BOTH, expand=True)
        root.columnconfigure(0, weight=1)
        root.rowconfigure(3, weight=1)

        connection = ttk.LabelFrame(root, text="本地 API", padding=8)
        connection.grid(row=0, column=0, sticky="ew")
        connection.columnconfigure(1, weight=1)
        ttk.Label(connection, text="地址").grid(row=0, column=0, padx=(0, 8))
        self.api_entry = ttk.Entry(connection, textvariable=self.api_base_var)
        self.api_entry.grid(row=0, column=1, sticky="ew")
        self.refresh_button = ttk.Button(connection, text="刷新", command=self.refresh)
        self.refresh_button.grid(row=0, column=2, padx=(8, 0))

        filters = ttk.LabelFrame(root, text="筛选", padding=8)
        filters.grid(row=1, column=0, sticky="ew", pady=(10, 0))
        specs = (
            ("综合搜索", self.keyword_var, 22),
            ("创建人/更新人", self.owner_var, 18),
            ("人员编号", self.personnel_var, 12),
            ("任务 ID", self.task_id_var, 12),
            ("事务码", self.tcode_var, 10),
        )
        column = 0
        for label, variable, width in specs:
            ttk.Label(filters, text=label).grid(row=0, column=column, padx=(0, 4), sticky="w")
            entry = ttk.Entry(filters, textvariable=variable, width=width)
            entry.grid(row=0, column=column + 1, padx=(0, 12), sticky="ew")
            entry.bind("<KeyRelease>", lambda _event: self.apply_filters())
            column += 2
        filters.columnconfigure(1, weight=1)
        ttk.Button(filters, text="清空", command=self.clear_filters).grid(row=0, column=column)

        toolbar = ttk.Frame(root)
        toolbar.grid(row=2, column=0, sticky="ew", pady=(10, 6))
        ttk.Label(toolbar, textvariable=self.summary_var).pack(side=tk.LEFT)
        self.delete_button = ttk.Button(toolbar, text="删除所选任务", command=self.delete_selected)
        self.delete_button.pack(side=tk.RIGHT)
        ttk.Button(toolbar, text="清除选择", command=self.clear_selection).pack(side=tk.RIGHT, padx=(0, 8))
        ttk.Button(toolbar, text="全选当前结果", command=self.select_all_visible).pack(side=tk.RIGHT, padx=(0, 8))

        panes = ttk.Panedwindow(root, orient=tk.VERTICAL)
        panes.grid(row=3, column=0, sticky="nsew")

        table_frame = ttk.Frame(panes)
        table_frame.rowconfigure(0, weight=1)
        table_frame.columnconfigure(0, weight=1)
        columns = ("id", "name", "tcode", "owner", "personnel", "status", "schedule", "next")
        self.tree = ttk.Treeview(table_frame, columns=columns, show="headings", selectmode="extended")
        headings = {
            "id": "任务 ID",
            "name": "任务名称",
            "tcode": "事务码",
            "owner": "设置人",
            "personnel": "人员编号",
            "status": "状态",
            "schedule": "执行计划",
            "next": "下次执行",
        }
        widths = {"id": 90, "name": 250, "tcode": 90, "owner": 180, "personnel": 105, "status": 65, "schedule": 135, "next": 155}
        for key in columns:
            self.tree.heading(key, text=headings[key], command=lambda k=key: self._sort_by(k))
            self.tree.column(key, width=widths[key], minwidth=55, stretch=key in {"name", "owner"})
        yscroll = ttk.Scrollbar(table_frame, orient=tk.VERTICAL, command=self.tree.yview)
        xscroll = ttk.Scrollbar(table_frame, orient=tk.HORIZONTAL, command=self.tree.xview)
        self.tree.configure(yscrollcommand=yscroll.set, xscrollcommand=xscroll.set)
        self.tree.grid(row=0, column=0, sticky="nsew")
        yscroll.grid(row=0, column=1, sticky="ns")
        xscroll.grid(row=1, column=0, sticky="ew")
        self.tree.bind("<<TreeviewSelect>>", self._show_selected_detail)
        panes.add(table_frame, weight=3)

        detail_frame = ttk.LabelFrame(panes, text="任务详情（仅展示安全业务字段）", padding=6)
        detail_frame.rowconfigure(0, weight=1)
        detail_frame.columnconfigure(0, weight=1)
        self.detail = tk.Text(detail_frame, height=10, wrap="word", state=tk.DISABLED, font=("Microsoft YaHei UI", 9))
        detail_scroll = ttk.Scrollbar(detail_frame, orient=tk.VERTICAL, command=self.detail.yview)
        self.detail.configure(yscrollcommand=detail_scroll.set)
        self.detail.grid(row=0, column=0, sticky="nsew")
        detail_scroll.grid(row=0, column=1, sticky="ns")
        panes.add(detail_frame, weight=1)

        status = ttk.Label(root, textvariable=self.status_var, relief=tk.SUNKEN, anchor="w", padding=(6, 4))
        status.grid(row=4, column=0, sticky="ew", pady=(8, 0))

    def _client(self) -> ScheduleApiClient:
        return ScheduleApiClient(self.api_base_var.get())

    def _run_worker(self, action: str, func: Callable[[], object]) -> None:
        if self.busy:
            return
        self.busy = True
        self._set_controls_enabled(False)

        def work() -> None:
            try:
                self.results.put(WorkerResult(action=action, value=func()))
            except Exception as exc:  # UI boundary: present safe client exceptions to the user.
                self.results.put(WorkerResult(action=action, error=exc))

        threading.Thread(target=work, daemon=True).start()

    def _poll_worker_results(self) -> None:
        try:
            while True:
                result = self.results.get_nowait()
                self.busy = False
                self._set_controls_enabled(True)
                if result.error:
                    self.status_var.set(f"操作失败：{result.error}")
                    messagebox.showerror("操作失败", str(result.error), parent=self)
                elif result.action == "refresh":
                    self.tasks = list(result.value or [])
                    self.task_by_id = {task.task_id: task for task in self.tasks}
                    self.apply_filters()
                    self.status_var.set(f"已从 {normalize_api_base(self.api_base_var.get())} 加载 {len(self.tasks)} 个任务")
                elif result.action == "delete":
                    deleted, removed_queued, failures = result.value  # type: ignore[misc]
                    self.status_var.set(f"已删除 {deleted} 个任务；同步移除 {removed_queued} 个排队任务")
                    text = f"已删除 {deleted} 个定时任务。\n已同步移除 {removed_queued} 个尚未执行的排队任务。"
                    if failures:
                        text += "\n\n删除失败：\n" + "\n".join(failures)
                        messagebox.showwarning("删除完成（有失败项）", text, parent=self)
                    else:
                        messagebox.showinfo("删除完成", text, parent=self)
                    self.refresh()
        except queue.Empty:
            pass
        self.after(100, self._poll_worker_results)

    def _set_controls_enabled(self, enabled: bool) -> None:
        state = tk.NORMAL if enabled else tk.DISABLED
        self.refresh_button.configure(state=state)
        self.delete_button.configure(state=state)
        self.api_entry.configure(state=state)

    def refresh(self) -> None:
        if self.busy:
            return
        try:
            api_base = normalize_api_base(self.api_base_var.get())
        except ValueError as exc:
            messagebox.showerror("API 地址无效", str(exc), parent=self)
            return
        self.api_base_var.set(api_base)
        self.status_var.set("正在读取全部定时任务...")
        client = ScheduleApiClient(api_base)
        self._run_worker("refresh", client.list_schedules)

    def clear_filters(self) -> None:
        for variable in (self.keyword_var, self.owner_var, self.personnel_var, self.task_id_var, self.tcode_var):
            variable.set("")
        self.apply_filters()

    def apply_filters(self) -> None:
        filters = ScheduleFilters(
            keyword=self.keyword_var.get(),
            owner=self.owner_var.get(),
            personnel_number=self.personnel_var.get(),
            task_id=self.task_id_var.get(),
            tcode=self.tcode_var.get(),
        )
        self.visible_tasks = filter_tasks(self.tasks, filters)
        self.tree.delete(*self.tree.get_children())
        for task in self.visible_tasks:
            schedule = " ".join(part for part in (task.frequency, task.weekday_label, task.run_time) if part)
            self.tree.insert(
                "",
                tk.END,
                iid=task.task_id,
                values=(
                    task.task_id,
                    task.name,
                    task.tcode,
                    task.owner,
                    task.personnel_text,
                    "启用" if task.enabled else "停用",
                    schedule,
                    task.next_run_at,
                ),
            )
        self.summary_var.set(f"显示 {len(self.visible_tasks)} / 共 {len(self.tasks)} 个任务")
        self._set_detail("请选择一个任务查看详情；可按 Ctrl 或 Shift 多选后删除。")

    def _sort_by(self, column: str) -> None:
        rows = [(self.tree.set(item, column).casefold(), item) for item in self.tree.get_children("")]
        reverse = getattr(self, "_last_sort", None) == (column, False)
        rows.sort(reverse=reverse)
        for position, (_value, item) in enumerate(rows):
            self.tree.move(item, "", position)
        self._last_sort = (column, reverse)

    def _show_selected_detail(self, _event: object = None) -> None:
        selected = self.tree.selection()
        if len(selected) == 1 and selected[0] in self.task_by_id:
            self._set_detail(self.task_by_id[selected[0]].detail_text())
        elif selected:
            self._set_detail(f"已选择 {len(selected)} 个任务。删除前会再次显示任务清单并要求确认。")
        else:
            self._set_detail("请选择一个任务查看详情。")

    def _set_detail(self, text: str) -> None:
        self.detail.configure(state=tk.NORMAL)
        self.detail.delete("1.0", tk.END)
        self.detail.insert("1.0", text)
        self.detail.configure(state=tk.DISABLED)

    def select_all_visible(self) -> None:
        self.tree.selection_set(self.tree.get_children())
        self._show_selected_detail()

    def clear_selection(self) -> None:
        self.tree.selection_remove(self.tree.selection())
        self._show_selected_detail()

    def delete_selected(self) -> None:
        selected_ids = list(self.tree.selection())
        if not selected_ids:
            messagebox.showinfo("未选择任务", "请先选择需要删除的定时任务。", parent=self)
            return
        preview = []
        for task_id in selected_ids[:12]:
            task = self.task_by_id.get(task_id)
            preview.append(f"• {task_id}  {task.name if task else ''}")
        if len(selected_ids) > 12:
            preview.append(f"• 其余 {len(selected_ids) - 12} 个任务")
        prompt = (
            f"确认彻底删除以下 {len(selected_ids)} 个定时任务？\n\n"
            + "\n".join(preview)
            + "\n\n删除会同步清理尚未开始的排队任务；正在运行和已完成的历史不会被删除。此操作不可撤销。"
        )
        if not messagebox.askyesno("确认删除", prompt, icon="warning", parent=self):
            return

        self.status_var.set(f"正在删除 {len(selected_ids)} 个任务...")
        client = self._client()

        def delete_all() -> tuple[int, int, list[str]]:
            deleted = 0
            removed_queued = 0
            failures: list[str] = []
            for task_id in selected_ids:
                try:
                    removed_queued += client.delete_schedule(task_id)
                    deleted += 1
                except Exception as exc:
                    failures.append(f"{task_id}：{exc}")
            return deleted, removed_queued, failures

        self._run_worker("delete", delete_all)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="SAP RPA 定时任务集中管理工具")
    parser.add_argument(
        "--api-base",
        help=f"本地 API 根地址；默认读取环境变量 {API_BASE_ENV}，否则使用 {DEFAULT_API_BASE}",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    app = ScheduleAdminApp(args.api_base)
    app.mainloop()


if __name__ == "__main__":
    main()
