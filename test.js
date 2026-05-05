#!/usr/bin/env node

/**
 * Copyright (c) 2024 John Jung
 * Copyright (c) 2026 Wiseair S.r.l.
 *
 * Smoke test for the Vitally MCP server.
 *
 * Spawns the built server, speaks JSON-RPC over stdin/stdout, and verifies:
 *   - tools/list returns every tool defined in TOOL_DEFINITIONS
 *   - search_accounts returns a full account payload (traits/MRR present)
 *   - get_account works against the demo "Sace" record
 *   - list_accounts returns paginated rows with full payload
 *   - update_account round-trips a custom trait, then clears it
 *   - list_tasks returns workspace-level tasks
 *   - search_tools auto-discovers list_accounts (registry not hand-maintained)
 *
 * Runs against demo mode by default. Set VITALLY_API_KEY to point at real
 * Vitally credentials — the asserts that depend on demo-only mock data
 * (Sace ARR=7000, MRR=583.33) are skipped in that case.
 */

import { spawn } from "node:child_process";
import * as path from "node:path";
import * as fs from "node:fs";
import { fileURLToPath } from "node:url";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const serverPath = path.resolve(__dirname, "build/index.js");

if (!fs.existsSync(serverPath)) {
  console.error(`Server script not found at ${serverPath}. Run "npm run build" first.`);
  process.exit(1);
}

const useRealApi = !!(process.env.VITALLY_API_KEY && process.env.VITALLY_API_KEY !== "your_api_key_here");
const env = { ...process.env };
if (!useRealApi) {
  delete env.VITALLY_API_KEY;
}

const server = spawn("node", [serverPath], {
  stdio: ["pipe", "pipe", "pipe"],
  env,
});

let buffer = "";
const pending = new Map();
let nextId = 1;

server.stdout.on("data", chunk => {
  buffer += chunk.toString("utf8");
  let idx;
  while ((idx = buffer.indexOf("\n")) !== -1) {
    const line = buffer.slice(0, idx).trim();
    buffer = buffer.slice(idx + 1);
    if (!line) continue;
    let msg;
    try {
      msg = JSON.parse(line);
    } catch {
      continue;
    }
    if (msg.id != null && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.error) reject(new Error(JSON.stringify(msg.error)));
      else resolve(msg.result);
    }
  }
});

server.stderr.on("data", chunk => process.stderr.write(chunk));
server.on("exit", code => {
  if (pending.size > 0) {
    for (const { reject } of pending.values()) reject(new Error(`server exited (code ${code}) with pending requests`));
  }
});

function rpc(method, params) {
  const id = nextId++;
  const payload = JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n";
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    server.stdin.write(payload);
    setTimeout(() => {
      if (pending.has(id)) {
        pending.delete(id);
        reject(new Error(`Timeout on ${method}`));
      }
    }, 15000);
  });
}

async function callTool(name, args = {}) {
  const result = await rpc("tools/call", { name, arguments: args });
  const text = result?.content?.[0]?.text;
  if (typeof text !== "string") return result;
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

const failures = [];
function check(label, ok, detail) {
  if (ok) {
    console.log(`  ok   ${label}`);
  } else {
    console.log(`  FAIL ${label}${detail ? ` — ${detail}` : ""}`);
    failures.push(label);
  }
}

async function main() {
  await rpc("initialize", {
    protocolVersion: "2024-11-05",
    capabilities: {},
    clientInfo: { name: "vitally-mcp-smoke", version: "1.0.0" },
  });

  console.log(`Mode: ${useRealApi ? "REAL Vitally API" : "demo (no API key)"}`);

  console.log("\n== tools/list ==");
  const toolsList = await rpc("tools/list", {});
  const toolNames = (toolsList.tools || []).map(t => t.name);
  const required = [
    "search_tools", "search_users", "get_user",
    "search_accounts", "find_account_by_name", "get_account", "list_accounts", "update_account",
    "get_account_health", "get_account_conversations", "list_conversations",
    "get_account_tasks", "list_tasks",
    "get_account_notes", "list_notes", "get_note_by_id", "create_account_note",
    "list_projects", "get_project", "list_organizations", "refresh_accounts",
  ];
  for (const t of required) {
    check(`tool registered: ${t}`, toolNames.includes(t), `present: ${toolNames.join(", ")}`);
  }

  console.log("\n== search_tools auto-discovery ==");
  const searchToolsResult = await callTool("search_tools", { keyword: "list_accounts" });
  check(
    "search_tools surfaces list_accounts (registry not hand-maintained)",
    Array.isArray(searchToolsResult.tools) && searchToolsResult.tools.some(t => t.name === "list_accounts")
  );

  console.log("\n== list_accounts (full payload) ==");
  const listed = await callTool("list_accounts", { limit: 5, status: "active" });
  check("list_accounts returns results array", Array.isArray(listed.results));
  if (Array.isArray(listed.results) && listed.results.length > 0) {
    const first = listed.results[0];
    check("list_accounts row has id+name", !!first.id && !!first.name);
    check("list_accounts row carries traits / MRR / health fields when present",
      "traits" in first || "mrr" in first || "healthScore" in first);
  }

  if (!useRealApi) {
    console.log("\n== demo: search_accounts(name='Sace') has full traits ==");
    const sace = await callTool("search_accounts", { name: "Sace" });
    const saceAccount = sace.accounts?.[0];
    check("Sace found", !!saceAccount, JSON.stringify(sace));
    if (saceAccount) {
      check("Sace has traits['vitally.custom.arr'] = 7000",
        saceAccount.traits?.["vitally.custom.arr"] === 7000,
        JSON.stringify(saceAccount.traits));
      check("Sace mrr ≈ 583.33",
        Math.abs((saceAccount.mrr ?? 0) - 583.33) < 0.01,
        `mrr=${saceAccount.mrr}`);
    }

    console.log("\n== demo: get_account by externalId ==");
    const got = await callTool("get_account", { externalId: "sace" });
    check("get_account returns Sace by externalId", got?.name === "Sace", JSON.stringify(got));

    console.log("\n== demo: update_account round-trip ==");
    const setRes = await callTool("update_account", {
      accountId: "4",
      traits: { "vitally.custom.testTrait": "claude-code-was-here" },
    });
    check("update_account returns updated traits",
      setRes?.traits?.["vitally.custom.testTrait"] === "claude-code-was-here",
      JSON.stringify(setRes?.traits));
    const readBack = await callTool("get_account", { accountId: "4" });
    check("get_account reflects the updated trait",
      readBack?.traits?.["vitally.custom.testTrait"] === "claude-code-was-here",
      JSON.stringify(readBack?.traits));
    const cleanRes = await callTool("update_account", {
      accountId: "4",
      traits: { "vitally.custom.testTrait": null },
    });
    check("update_account clears the trait when set to null",
      cleanRes?.traits?.["vitally.custom.testTrait"] === null,
      JSON.stringify(cleanRes?.traits));
  } else {
    console.log("\n== skipping Sace-specific demo asserts (real API mode) ==");
  }

  console.log("\n== list_tasks ==");
  const tasks = await callTool("list_tasks", { limit: 5 });
  check("list_tasks returns results array", Array.isArray(tasks.results), JSON.stringify(tasks).slice(0, 200));

  console.log("\n== includeTraits=false slim mode ==");
  const slim = await callTool("list_accounts", { limit: 2, includeTraits: false });
  if (Array.isArray(slim.results) && slim.results.length > 0) {
    const row = slim.results[0];
    const slimKeys = Object.keys(row).sort().join(",");
    check("slim row has only id,name,externalId,uri", slimKeys === "externalId,id,name,uri", slimKeys);
  } else {
    check("slim mode returned results", false, JSON.stringify(slim));
  }

  console.log(`\n${failures.length === 0 ? "All smoke checks passed." : `${failures.length} FAILURE(S)`}`);
  server.kill();
  process.exit(failures.length === 0 ? 0 : 1);
}

main().catch(err => {
  console.error("Smoke test crashed:", err);
  server.kill();
  process.exit(2);
});
