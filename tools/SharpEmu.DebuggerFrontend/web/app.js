/*
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
*/

"use strict";

const REGISTER_ORDER = [
  "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi",
  "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15",
  "rip", "rflags", "fs_base", "gs_base",
];

const ui = {
  cursor: 0,
  snapshot: null,
  activity: [],
  polling: false,
  refreshing: false,
};

const byId = (id) => document.getElementById(id);

async function api(path, options = {}) {
  const response = await fetch(path, {
    headers: { "Content-Type": "application/json" },
    cache: "no-store",
    ...options,
  });
  let payload;
  try {
    payload = await response.json();
  } catch {
    throw new Error(`Frontend returned HTTP ${response.status}.`);
  }
  if (!response.ok) {
    throw new Error(payload.error || `Request failed with HTTP ${response.status}.`);
  }
  return payload;
}

async function pollSnapshot() {
  if (ui.polling) return;
  ui.polling = true;
  try {
    const snapshot = await api(`/api/snapshot?since=${ui.cursor}`);
    applySnapshot(snapshot);
  } catch (error) {
    showToast("Frontend unavailable", error.message, "error");
  } finally {
    ui.polling = false;
  }
}

function applySnapshot(snapshot) {
  const previous = ui.snapshot;
  const previousCursor = ui.cursor;
  const newMessages = (snapshot.messages || []).filter((message) => message.id > previousCursor);
  ui.snapshot = snapshot;
  ui.cursor = snapshot.cursor;
  if (newMessages.length) {
    ui.activity.push(...newMessages);
    ui.activity = ui.activity.slice(-350);
    renderActivity();
    for (const message of newMessages) {
      handleActivityNotification(message);
    }
  }

  if (!previous && snapshot.defaultEndpoint) {
    byId("debug-host").value = snapshot.defaultEndpoint.host;
    byId("debug-port").value = snapshot.defaultEndpoint.port;
  }

  renderConnection(snapshot);
  renderEmulator(snapshot.emulator || {});
  renderTarget(snapshot);
  renderRegisters(snapshot.registers || {});
  renderBreakpoints(snapshot.breakpoints || []);
  updateControls(snapshot);
}

function renderConnection(snapshot) {
  const status = byId("connection-status");
  status.classList.toggle("connected", snapshot.connected);
  status.classList.toggle("disconnected", !snapshot.connected);
  byId("connection-state").textContent = snapshot.connected ? "Connected" : "Offline";
  byId("connection-endpoint").textContent = snapshot.endpoint || "Not connected";
  byId("connection-button-label").textContent = snapshot.connected ? "Disconnect" : "Connect";
  byId("connection-button").classList.toggle("button-danger", snapshot.connected);
  byId("connection-button").classList.toggle("button-primary", !snapshot.connected);
  byId("protocol-version").textContent = snapshot.protocol || "—";
}

function renderEmulator(emulator) {
  const statusWrap = byId("emulator-status").closest(".launch-status");
  statusWrap.classList.toggle("running", Boolean(emulator.running));
  statusWrap.classList.toggle("exited", !emulator.running && emulator.exitCode !== null && emulator.exitCode !== undefined);
  if (emulator.running) {
    byId("emulator-status").textContent = `SharpEmu running · PID ${emulator.pid}`;
    byId("emulator-detail").textContent = emulator.eboot || "Launching game executable";
  } else if (emulator.exitCode !== null && emulator.exitCode !== undefined) {
    byId("emulator-status").textContent = `SharpEmu exited · code ${emulator.exitCode}`;
    byId("emulator-detail").textContent = emulator.eboot || "The launched process has ended.";
  } else {
    byId("emulator-status").textContent = "No frontend-launched session";
    byId("emulator-detail").textContent = "You can still attach to an emulator that is already running.";
  }
  if (emulator.eboot && document.activeElement !== byId("eboot-path")) {
    byId("eboot-path").value = emulator.eboot;
  }
  byId("eboot-path").disabled = Boolean(emulator.running);
  byId("browse-eboot-button").disabled = Boolean(emulator.running);
  byId("launch-button").disabled = Boolean(emulator.running);
  byId("stop-emulator-button").disabled = !emulator.running;
}

function renderTarget(snapshot) {
  const state = snapshot.state || "Disconnected";
  const stop = snapshot.lastStop || {};
  const registers = snapshot.registers || {};
  byId("toolbar-target-state").textContent = state;
  const badge = byId("target-state-badge");
  badge.textContent = state;
  badge.className = `state-badge ${state.toLowerCase()}`;
  byId("target-address").textContent = stop.address || registers.rip || "—";
  byId("stop-reason").textContent = stop.reason || "—";
  byId("frame-kind").textContent = stop.frameKind || "—";
  byId("frame-label").textContent = stop.frameLabel || "—";
  byId("stop-result").textContent = stop.result || "—";
  byId("opcode-bytes").textContent = stop.opcodeBytes || "—";
  const detailWrap = byId("stop-detail-wrap");
  detailWrap.classList.toggle("hidden", !stop.detail);
  byId("stop-detail").textContent = stop.detail || "";
  renderStallAnalysis(stop.analysis);
}

function renderStallAnalysis(analysis) {
  const panel = byId("stall-analysis");
  if (!analysis) {
    panel.classList.add("hidden");
    return;
  }
  panel.classList.remove("hidden");
  byId("stall-analysis-title").textContent = analysis.title || "Execution stall detected";
  byId("stall-confidence").textContent = `${analysis.confidence || "Medium"} confidence`;
  byId("stall-summary").textContent = analysis.summary || "The guest is not making forward progress.";
  byId("stall-cause").textContent = analysis.cause || "The repeated path is not changing the state checked by the guest.";
  byId("stall-fix").textContent = analysis.fix || "Trace the repeated import and implement its missing state transition.";

  const actions = (analysis.actions || []).map((text) => createTextElement("li", text));
  byId("stall-actions").replaceChildren(...actions);
  const evidence = (analysis.evidence || []).map((text) => createTextElement("li", text));
  byId("stall-evidence").replaceChildren(...evidence);
}

function renderRegisters(registers) {
  const grid = byId("register-grid");
  if (!Object.keys(registers).length) {
    grid.className = "register-grid empty-state";
    grid.replaceChildren(createTextElement("p", "Pause the target to inspect registers."));
    return;
  }

  grid.className = "register-grid";
  const fragment = document.createDocumentFragment();
  for (const name of REGISTER_ORDER) {
    const value = registers[name] ?? "—";
    const item = document.createElement("div");
    item.className = `register-item ${["rip", "rflags", "fs_base", "gs_base"].includes(name) ? "special" : ""}`;
    const label = createTextElement("span", name);
    label.className = "register-name";
    const button = createTextElement("button", value);
    button.type = "button";
    button.className = "register-value";
    button.dataset.register = name;
    button.title = `Edit ${name}`;
    button.addEventListener("click", () => openRegisterDialog(name, value));
    item.append(label, button);
    fragment.append(item);
  }
  grid.replaceChildren(fragment);
}

function renderBreakpoints(breakpoints) {
  byId("breakpoint-count").textContent = breakpoints.length;
  const body = byId("breakpoint-table");
  if (!breakpoints.length) {
    const row = document.createElement("tr");
    row.className = "empty-row";
    const cell = createTextElement("td", "No breakpoints configured.");
    cell.colSpan = 6;
    row.append(cell);
    body.replaceChildren(row);
    return;
  }

  const fragment = document.createDocumentFragment();
  for (const breakpoint of breakpoints) {
    const row = document.createElement("tr");
    const enabledCell = document.createElement("td");
    const toggle = document.createElement("input");
    toggle.type = "checkbox";
    toggle.className = "switch";
    toggle.checked = Boolean(breakpoint.enabled);
    toggle.title = toggle.checked ? "Disable breakpoint" : "Enable breakpoint";
    toggle.addEventListener("change", async () => {
      toggle.disabled = true;
      try {
        await sendCommand({ command: "enable-breakpoint", id: breakpoint.id, enabled: toggle.checked });
        await refreshBreakpoints();
      } catch (error) {
        toggle.checked = !toggle.checked;
        showToast("Breakpoint update failed", error.message, "error");
      } finally {
        toggle.disabled = false;
      }
    });
    enabledCell.append(toggle);

    const idCell = createTextElement("td", String(breakpoint.id));
    const kindCell = document.createElement("td");
    const kind = createTextElement("span", breakpoint.kind);
    kind.className = "kind-pill";
    kindCell.append(kind);
    const addressCell = createTextElement("td", breakpoint.address);
    const lengthCell = createTextElement("td", String(breakpoint.length));
    const actionCell = document.createElement("td");
    const remove = createTextElement("button", "Remove");
    remove.type = "button";
    remove.className = "row-action";
    remove.addEventListener("click", async () => {
      remove.disabled = true;
      try {
        await sendCommand({ command: "remove-breakpoint", id: breakpoint.id });
        await refreshBreakpoints();
        showToast("Breakpoint removed", `Breakpoint ${breakpoint.id} was removed.`, "success");
      } catch (error) {
        showToast("Remove failed", error.message, "error");
      } finally {
        remove.disabled = false;
      }
    });
    actionCell.append(remove);
    row.append(enabledCell, idCell, kindCell, addressCell, lengthCell, actionCell);
    fragment.append(row);
  }
  body.replaceChildren(fragment);
}

function updateControls(snapshot) {
  const connected = Boolean(snapshot.connected);
  const state = String(snapshot.state || "").toLowerCase();
  const paused = connected && state === "paused";
  const running = connected && state === "running";
  byId("continue-button").disabled = !paused;
  byId("step-button").disabled = !paused;
  byId("pause-button").disabled = !running;
  byId("refresh-button").disabled = !connected;
  for (const id of [
    "breakpoint-address", "breakpoint-kind", "breakpoint-length",
    "memory-address", "memory-length", "memory-write-address", "memory-write-bytes",
    "raw-command-input",
  ]) {
    byId(id).disabled = !connected || (["memory-address", "memory-length", "memory-write-address", "memory-write-bytes"].includes(id) && !paused);
  }
  byId("breakpoint-form").querySelector("button").disabled = !connected;
  byId("memory-read-form").querySelector("button").disabled = !paused;
  byId("memory-write-form").querySelector("button").disabled = !paused;
  byId("raw-command-form").querySelector("button").disabled = !connected;
}

async function sendCommand(request) {
  const payload = await api("/api/command", {
    method: "POST",
    body: JSON.stringify({ request }),
  });
  const response = payload.response;
  if (!response?.ok) {
    throw new Error(response?.error || `${request.command} failed.`);
  }
  await pollSnapshot();
  return response;
}

async function refreshTarget() {
  if (!ui.snapshot?.connected || ui.refreshing) return;
  ui.refreshing = true;
  byId("refresh-button").disabled = true;
  try {
    const status = await sendCommand({ command: "status" });
    if (String(status.data?.state).toLowerCase() === "paused") {
      await sendCommand({ command: "registers" });
    }
    await sendCommand({ command: "list-breakpoints" });
  } catch (error) {
    showToast("Refresh failed", error.message, "error");
  } finally {
    ui.refreshing = false;
    await pollSnapshot();
  }
}

async function refreshBreakpoints() {
  await sendCommand({ command: "list-breakpoints" });
}

async function connectOrDisconnect(event) {
  event.preventDefault();
  const button = byId("connection-button");
  button.disabled = true;
  try {
    if (ui.snapshot?.connected) {
      const snapshot = await api("/api/disconnect", { method: "POST", body: "{}" });
      applySnapshot(snapshot);
      showToast("Disconnected", "The debugger connection was closed.");
      return;
    }
    const host = byId("debug-host").value.trim();
    const port = Number.parseInt(byId("debug-port").value, 10);
    const snapshot = await api("/api/connect", {
      method: "POST",
      body: JSON.stringify({ host, port }),
    });
    applySnapshot(snapshot);
    showToast("Debugger connected", `Attached to ${host}:${port}.`, "success");
    await refreshTarget();
  } catch (error) {
    showToast("Connection failed", error.message, "error");
    await pollSnapshot();
  } finally {
    button.disabled = false;
  }
}

async function browseForEboot() {
  const button = byId("browse-eboot-button");
  button.disabled = true;
  const previousLabel = button.textContent;
  button.textContent = "Choosing…";
  try {
    const result = await api("/api/select-eboot", {
      method: "POST",
      body: JSON.stringify({ initialPath: byId("eboot-path").value.trim() || null }),
    });
    if (result.path) {
      byId("eboot-path").value = result.path;
      byId("eboot-path").focus();
      byId("eboot-path").setSelectionRange(result.path.length, result.path.length);
    }
  } catch (error) {
    showToast("File picker unavailable", error.message, "error");
  } finally {
    button.textContent = previousLabel;
    button.disabled = Boolean(ui.snapshot?.emulator?.running);
  }
}

async function launchEboot(event) {
  event.preventDefault();
  const ebootPath = byId("eboot-path").value.trim();
  const debugPort = Number.parseInt(byId("debug-port").value, 10);
  const button = byId("launch-button");
  const previousHtml = button.innerHTML;
  button.disabled = true;
  button.textContent = "Starting SharpEmu…";
  showToast("Launching SharpEmu", "Waiting for the local debug server to become ready.");
  try {
    const snapshot = await api("/api/launch", {
      method: "POST",
      body: JSON.stringify({ ebootPath, debugPort }),
    });
    byId("debug-host").value = "127.0.0.1";
    applySnapshot(snapshot);
    showToast("Launch complete", "SharpEmu is running and the debugger attached automatically.", "success");
    await refreshTarget();
  } catch (error) {
    showToast("Launch failed", error.message, "error");
    await pollSnapshot();
  } finally {
    button.innerHTML = previousHtml;
    button.disabled = Boolean(ui.snapshot?.emulator?.running);
  }
}

async function stopEmulator() {
  if (!window.confirm("Stop the SharpEmu process launched by this frontend?")) return;
  const button = byId("stop-emulator-button");
  button.disabled = true;
  button.textContent = "Stopping…";
  try {
    const snapshot = await api("/api/stop-emulator", { method: "POST", body: "{}" });
    applySnapshot(snapshot);
    showToast("SharpEmu stopped", "The frontend-launched emulator process was stopped.");
  } catch (error) {
    showToast("Stop failed", error.message, "error");
  } finally {
    button.textContent = "Stop";
    button.disabled = !ui.snapshot?.emulator?.running;
  }
}

async function executionCommand(command, label) {
  try {
    await sendCommand({ command });
    showToast(label, command === "pause" ? "Pause requested at the next frame boundary." : `${label} command accepted.`, "success");
  } catch (error) {
    showToast(`${label} failed`, error.message, "error");
  }
}

function openRegisterDialog(name, value) {
  if (String(ui.snapshot?.state).toLowerCase() !== "paused") return;
  byId("register-name").value = name;
  byId("register-value").value = value;
  byId("register-dialog").showModal();
  byId("register-value").focus();
  byId("register-value").select();
}

async function applyRegister(event) {
  event.preventDefault();
  const name = byId("register-name").value;
  const value = byId("register-value").value.trim();
  try {
    await sendCommand({ command: "set-register", register: name, value });
    await sendCommand({ command: "registers" });
    byId("register-dialog").close();
    showToast("Register updated", `${name.toUpperCase()} is now ${value}.`, "success");
  } catch (error) {
    showToast("Register update failed", error.message, "error");
  }
}

async function addBreakpoint(event) {
  event.preventDefault();
  const address = byId("breakpoint-address").value.trim();
  const kind = byId("breakpoint-kind").value;
  const length = Number.parseInt(byId("breakpoint-length").value, 10);
  try {
    await sendCommand({ command: "add-breakpoint", address, kind, length });
    await refreshBreakpoints();
    byId("breakpoint-address").value = "";
    showToast("Breakpoint added", `${kind} breakpoint created at ${address}.`, "success");
  } catch (error) {
    showToast("Breakpoint failed", error.message, "error");
  }
}

async function readMemory(event) {
  event.preventDefault();
  const address = byId("memory-address").value.trim();
  const length = Number.parseInt(byId("memory-length").value, 10);
  try {
    const response = await sendCommand({ command: "read-memory", address, length });
    const data = response.data || {};
    byId("memory-view").textContent = formatHexDump(data.address || address, data.bytes || "");
    byId("memory-meta").textContent = `${data.length || 0} bytes from ${data.address || address}`;
    if (!byId("memory-write-address").value) byId("memory-write-address").value = data.address || address;
  } catch (error) {
    showToast("Memory read failed", error.message, "error");
  }
}

async function writeMemory(event) {
  event.preventDefault();
  const address = byId("memory-write-address").value.trim();
  const bytes = byId("memory-write-bytes").value.replace(/\s+/g, "");
  if (!bytes || bytes.length % 2 || !/^[0-9a-f]+$/i.test(bytes)) {
    showToast("Invalid bytes", "Enter an even number of hexadecimal digits.", "error");
    return;
  }
  try {
    const response = await sendCommand({ command: "write-memory", address, bytes });
    showToast("Memory written", `${response.data?.written || bytes.length / 2} bytes written at ${address}.`, "success");
    if (byId("memory-address").value.trim() === address) {
      byId("memory-read-form").requestSubmit();
    }
  } catch (error) {
    showToast("Memory write failed", error.message, "error");
  }
}

function formatHexDump(startAddress, hex) {
  const clean = String(hex).replace(/\s+/g, "");
  if (!clean) return "No bytes returned.";
  const bytes = clean.match(/.{1,2}/g) || [];
  let base = 0n;
  try { base = BigInt(startAddress); } catch { /* display from zero */ }
  const lines = [];
  for (let offset = 0; offset < bytes.length; offset += 16) {
    const chunk = bytes.slice(offset, offset + 16);
    const address = `0x${(base + BigInt(offset)).toString(16).toUpperCase().padStart(16, "0")}`;
    const left = chunk.slice(0, 8).join(" ").padEnd(23, " ");
    const right = chunk.slice(8).join(" ").padEnd(23, " ");
    const ascii = chunk.map((value) => {
      const code = Number.parseInt(value, 16);
      return code >= 32 && code <= 126 ? String.fromCharCode(code) : ".";
    }).join("");
    lines.push(`${address}  ${left}  ${right}  |${ascii.padEnd(16, " ")}|`);
  }
  return lines.join("\n");
}

async function sendRawCommand(event) {
  event.preventDefault();
  try {
    const request = JSON.parse(byId("raw-command-input").value);
    if (!request || Array.isArray(request) || typeof request !== "object") throw new Error("Request must be a JSON object.");
    await sendCommand(request);
    showToast("Raw request sent", request.command || "Request completed.", "success");
  } catch (error) {
    showToast("Raw request failed", error.message, "error");
  }
}

function renderActivity() {
  const stream = byId("activity-stream");
  const query = byId("activity-search").value.trim().toLowerCase();
  const messages = query
    ? ui.activity.filter((item) => `${item.kind} ${item.summary} ${JSON.stringify(item.payload || "")}`.toLowerCase().includes(query))
    : ui.activity;
  if (!messages.length) {
    const empty = createTextElement("div", query ? "No activity matches this filter." : "Protocol events and commands will appear here.");
    empty.className = "activity-empty";
    stream.replaceChildren(empty);
    return;
  }

  const nearBottom = stream.scrollHeight - stream.scrollTop - stream.clientHeight < 70;
  const fragment = document.createDocumentFragment();
  for (const item of messages.slice(-250)) {
    const row = document.createElement("div");
    row.className = `activity-row ${item.kind}`;
    const timestamp = createTextElement("span", item.time);
    timestamp.className = "activity-time";
    const kind = createTextElement("span", item.kind);
    kind.className = "activity-kind";
    const summary = document.createElement("div");
    summary.className = "activity-summary";
    summary.append(createTextElement("span", item.summary));
    if (item.payload !== undefined) {
      const details = document.createElement("details");
      details.append(createTextElement("summary", "View payload"));
      const pre = createTextElement("pre", JSON.stringify(item.payload, null, 2));
      details.append(pre);
      summary.append(details);
    }
    row.append(timestamp, kind, summary);
    fragment.append(row);
  }
  stream.replaceChildren(fragment);
  if (nearBottom || !query) stream.scrollTop = stream.scrollHeight;
}

function handleActivityNotification(message) {
  if (message.kind === "event" && message.summary === "stopped") {
    const reason = message.payload?.reason || "Target stopped";
    showToast("Target paused", `${reason} at ${message.payload?.address || "unknown address"}.`);
  } else if (message.kind === "event" && message.summary === "terminated") {
    showToast("Target terminated", "The emulation run has completed.");
  } else if (message.kind === "emulator" && message.summary.startsWith("Emulator exited")) {
    showToast("Emulator exited", message.summary);
  }
}

function showToast(title, message, tone = "") {
  const toast = document.createElement("div");
  toast.className = `toast ${tone}`;
  toast.append(createTextElement("strong", title), createTextElement("span", message));
  byId("toast-stack").append(toast);
  window.setTimeout(() => toast.remove(), 4300);
}

function createTextElement(tag, text) {
  const element = document.createElement(tag);
  element.textContent = text == null ? "" : String(text);
  return element;
}

function bindEvents() {
  byId("connection-form").addEventListener("submit", connectOrDisconnect);
  byId("launch-form").addEventListener("submit", launchEboot);
  byId("browse-eboot-button").addEventListener("click", browseForEboot);
  byId("stop-emulator-button").addEventListener("click", stopEmulator);
  byId("continue-button").addEventListener("click", () => executionCommand("continue", "Continue"));
  byId("pause-button").addEventListener("click", () => executionCommand("pause", "Pause"));
  byId("step-button").addEventListener("click", () => executionCommand("step", "Step"));
  byId("refresh-button").addEventListener("click", refreshTarget);
  byId("breakpoint-form").addEventListener("submit", addBreakpoint);
  byId("memory-read-form").addEventListener("submit", readMemory);
  byId("memory-write-form").addEventListener("submit", writeMemory);
  byId("raw-command-form").addEventListener("submit", sendRawCommand);
  byId("register-form").addEventListener("submit", applyRegister);
  byId("register-dialog-close").addEventListener("click", () => byId("register-dialog").close());
  byId("register-cancel").addEventListener("click", () => byId("register-dialog").close());
  byId("activity-search").addEventListener("input", renderActivity);
  byId("clear-activity").addEventListener("click", () => {
    ui.activity = [];
    renderActivity();
  });
  document.addEventListener("keydown", (event) => {
    if (["INPUT", "TEXTAREA", "SELECT"].includes(document.activeElement?.tagName)) return;
    if (event.key === "F5") {
      event.preventDefault();
      if (!byId("continue-button").disabled) executionCommand("continue", "Continue");
    } else if (event.key === "F6") {
      event.preventDefault();
      if (!byId("pause-button").disabled) executionCommand("pause", "Pause");
    } else if (event.key === "F10") {
      event.preventDefault();
      if (!byId("step-button").disabled) executionCommand("step", "Step");
    }
  });
}

async function initialize() {
  bindEvents();
  await pollSnapshot();
  window.setInterval(pollSnapshot, 700);
  window.setInterval(() => {
    if (ui.snapshot?.connected) refreshTarget();
  }, 7000);
}

initialize();
