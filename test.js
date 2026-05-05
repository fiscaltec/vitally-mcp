#!/usr/bin/env node

/**
 * Copyright (c) 2024 John Jung
 * Copyright (c) 2026 Wiseair S.r.l.
 *
 * Smoke test for the Vitally MCP server. Spawns the built server, speaks
 * JSON-RPC over stdin/stdout, and verifies behaviour end-to-end against the
 * demo-mode mock fixtures.
 *
 * Set VITALLY_API_KEY to point at real Vitally credentials — asserts that
 * depend on demo-only mock data are skipped in that case.
 */

import { spawn } from "node:child_process";
import * as path from "node:path";
import * as fs from "node:fs";
import { fileURLToPath } from "node:url";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const serverPath = path.resolve(__dirname, "build/index.js");

if (!fs.existsSync(serverPath)) {
  console.error(`Server script not found at ${serverPath}. Run "pnpm run build" first.`);
  process.exit(1);
}

const useRealApi = !!(process.env.VITALLY_API_KEY && process.env.VITALLY_API_KEY !== "your_api_key_here");

function spawnServer(extraEnv = {}) {
  const env = { ...process.env, ...extraEnv };
  if (!useRealApi) delete env.VITALLY_API_KEY;
  const child = spawn("node", [serverPath], { stdio: ["pipe", "pipe", "pipe"], env });
  const ctx = {
    child,
    buffer: "",
    pending: new Map(),
    nextId: 1,
  };
  child.stdout.on("data", chunk => {
    ctx.buffer += chunk.toString("utf8");
    let idx;
    while ((idx = ctx.buffer.indexOf("\n")) !== -1) {
      const line = ctx.buffer.slice(0, idx).trim();
      ctx.buffer = ctx.buffer.slice(idx + 1);
      if (!line) continue;
      let msg;
      try { msg = JSON.parse(line); } catch { continue; }
      if (msg.id != null && ctx.pending.has(msg.id)) {
        const { resolve, reject } = ctx.pending.get(msg.id);
        ctx.pending.delete(msg.id);
        if (msg.error) reject(new Error(JSON.stringify(msg.error)));
        else resolve(msg.result);
      }
    }
  });
  child.stderr.on("data", chunk => process.stderr.write(chunk));
  child.on("exit", code => {
    if (ctx.pending.size > 0) {
      for (const { reject } of ctx.pending.values()) reject(new Error(`server exited (code ${code}) with pending requests`));
    }
  });
  return ctx;
}

function rpc(ctx, method, params) {
  const id = ctx.nextId++;
  const payload = JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n";
  return new Promise((resolve, reject) => {
    ctx.pending.set(id, { resolve, reject });
    ctx.child.stdin.write(payload);
    setTimeout(() => {
      if (ctx.pending.has(id)) {
        ctx.pending.delete(id);
        reject(new Error(`Timeout on ${method}`));
      }
    }, 15000);
  });
}

async function callTool(ctx, name, args = {}) {
  const result = await rpc(ctx, "tools/call", { name, arguments: args });
  const text = result?.content?.[0]?.text;
  if (typeof text !== "string") return result;
  try { return JSON.parse(text); } catch { return text; }
}

const failures = [];
function check(label, ok, detail) {
  if (ok) console.log(`  ok   ${label}`);
  else {
    console.log(`  FAIL ${label}${detail ? ` — ${detail}` : ""}`);
    failures.push(label);
  }
}

async function expectThrow(promise, label, matcher) {
  try {
    const v = await promise;
    // tools/call can succeed at the RPC level but return an error in content;
    // accept either route.
    if (v && typeof v === "object" && (v.isError || v.error)) {
      const text = JSON.stringify(v);
      check(label, !matcher || matcher.test(text), text.slice(0, 200));
      return;
    }
    check(label, false, `expected an error, got ${JSON.stringify(v).slice(0, 200)}`);
  } catch (err) {
    const msg = err?.message ?? String(err);
    check(label, !matcher || matcher.test(msg), msg.slice(0, 200));
  }
}

async function initialize(ctx) {
  await rpc(ctx, "initialize", {
    protocolVersion: "2024-11-05",
    capabilities: {},
    clientInfo: { name: "vitally-mcp-smoke", version: "1.0.0" },
  });
}

async function main() {
  const ctx = spawnServer();
  await initialize(ctx);

  console.log(`Mode: ${useRealApi ? "REAL Vitally API" : "demo (no API key)"}`);

  // -------------------------------------------------------------------------
  // tools/list
  // -------------------------------------------------------------------------
  console.log("\n== tools/list ==");
  const toolsList = await rpc(ctx, "tools/list", {});
  const toolNames = (toolsList.tools || []).map(t => t.name);
  const required = [
    "search_tools", "search_users", "get_user",
    "search_accounts", "find_account_by_name", "get_account", "list_accounts", "update_account",
    "aggregate_accounts",
    "get_account_health", "get_account_conversations", "list_conversations",
    "get_account_tasks", "list_tasks",
    "get_account_notes", "list_notes", "get_note_by_id", "create_account_note",
    "list_projects", "get_project", "list_organizations", "refresh_accounts",
  ];
  for (const t of required) {
    check(`tool registered: ${t}`, toolNames.includes(t), `present: ${toolNames.join(", ")}`);
  }

  console.log("\n== search_tools auto-discovery ==");
  const searchToolsResult = await callTool(ctx, "search_tools", { keyword: "aggregate" });
  check(
    "search_tools surfaces aggregate_accounts (registry not hand-maintained)",
    Array.isArray(searchToolsResult.tools) && searchToolsResult.tools.some(t => t.name === "aggregate_accounts")
  );

  // -------------------------------------------------------------------------
  // list_accounts: full payload + slim mode
  // -------------------------------------------------------------------------
  console.log("\n== list_accounts (full payload) ==");
  const listed = await callTool(ctx, "list_accounts", { limit: 5, status: "active" });
  check("list_accounts returns results array", Array.isArray(listed.results));
  if (Array.isArray(listed.results) && listed.results.length > 0) {
    const first = listed.results[0];
    check("list_accounts row has id+name", !!first.id && !!first.name);
    check("list_accounts row carries traits / MRR / health fields when present",
      "traits" in first || "mrr" in first || "healthScore" in first);
  }

  console.log("\n== includeTraits=false slim mode ==");
  const slim = await callTool(ctx, "list_accounts", { limit: 2, includeTraits: false });
  if (Array.isArray(slim.results) && slim.results.length > 0) {
    const row = slim.results[0];
    const slimKeys = Object.keys(row).sort().join(",");
    check("slim row has only id,name,externalId,uri", slimKeys === "externalId,id,name,uri", slimKeys);
  } else {
    check("slim mode returned results", false, JSON.stringify(slim));
  }

  // -------------------------------------------------------------------------
  // P0 Task 2: traits projection
  // -------------------------------------------------------------------------
  if (!useRealApi) {
    console.log("\n== Task 2: traits projection ==");
    const projected = await callTool(ctx, "list_accounts", {
      limit: 3,
      traits: ["vitally.custom.arr"],
    });
    if (Array.isArray(projected.results) && projected.results.length > 0) {
      const row = projected.results[0];
      const traitKeys = Object.keys(row.traits || {});
      check(
        "traits projection returns only requested keys",
        traitKeys.length === 1 && traitKeys[0] === "vitally.custom.arr",
        traitKeys.join(",")
      );
      check(
        "projected row still has top-level fields (mrr, name)",
        typeof row.mrr === "number" && typeof row.name === "string"
      );
    } else {
      check("traits projection produced results", false, JSON.stringify(projected));
    }

    const conflict = await callTool(ctx, "list_accounts", {
      limit: 1,
      traits: ["vitally.custom.arr"],
      includeTraits: false,
    });
    check(
      "traits + includeTraits=false surfaces a warning",
      typeof conflict.warning === "string" && conflict.warning.includes("traits"),
      conflict.warning
    );
    check(
      "traits projection still wins when in conflict",
      Array.isArray(conflict.results)
        && conflict.results.length > 0
        && Object.keys(conflict.results[0].traits || {}).length === 1
    );
  }

  // -------------------------------------------------------------------------
  // P0 Task 1: includeAccount default false on per-account list endpoints
  // -------------------------------------------------------------------------
  if (!useRealApi) {
    console.log("\n== Task 1: account stripped by default; size shrinks ==");
    const tasksWithAccount = await callTool(ctx, "get_account_tasks", {
      accountId: "4",
      limit: 10,
      includeAccount: true,
      descriptionFormat: "html",
    });
    const tasksWithoutAccount = await callTool(ctx, "get_account_tasks", {
      accountId: "4",
      limit: 10,
      descriptionFormat: "html",
    });
    const sizeBig = JSON.stringify(tasksWithAccount).length;
    const sizeSmall = JSON.stringify(tasksWithoutAccount).length;
    check(
      "stripping `account` shrinks payload",
      sizeSmall < sizeBig,
      `with=${sizeBig} without=${sizeSmall}`
    );
    if (Array.isArray(tasksWithoutAccount.results) && tasksWithoutAccount.results.length > 0) {
      const row = tasksWithoutAccount.results[0];
      check("stripped task row has no account field", !("account" in row), Object.keys(row).join(","));
      check("stripped task row preserves accountId", typeof row.accountId === "string", row.accountId);
    }

    const notesStripped = await callTool(ctx, "list_notes", { limit: 5, descriptionFormat: "html" });
    if (Array.isArray(notesStripped.results) && notesStripped.results.length > 0) {
      const row = notesStripped.results[0];
      check("list_notes row has no account by default", !("account" in row));
      check("list_notes row preserves accountId", typeof row.accountId === "string");
    }

    const convStripped = await callTool(ctx, "list_conversations", { limit: 5 });
    if (Array.isArray(convStripped.results) && convStripped.results.length > 0) {
      const row = convStripped.results[0];
      check("list_conversations row has no account by default", !("account" in row));
    }
  }

  // -------------------------------------------------------------------------
  // P1 Task 3: status filter on get_account_tasks
  // -------------------------------------------------------------------------
  if (!useRealApi) {
    console.log("\n== Task 3: status filter on get_account_tasks ==");
    const open = await callTool(ctx, "get_account_tasks", { accountId: "4", status: "open", limit: 10 });
    check(
      "status=open returns only open tasks (completedAt null && archivedAt null)",
      Array.isArray(open.results) && open.results.every(t => !t.completedAt && !t.archivedAt) && open.results.length === 2,
      `count=${open.results?.length}`
    );
    const completed = await callTool(ctx, "get_account_tasks", { accountId: "4", status: "completed", limit: 10 });
    check(
      "status=completed returns only completed tasks",
      Array.isArray(completed.results) && completed.results.every(t => !!t.completedAt) && completed.results.length === 1
    );
    const archived = await callTool(ctx, "get_account_tasks", { accountId: "4", status: "archived", limit: 10 });
    check(
      "status=archived returns only archived tasks",
      Array.isArray(archived.results) && archived.results.every(t => !!t.archivedAt) && archived.results.length === 1
    );
  }

  // -------------------------------------------------------------------------
  // P1 Task 4: sortBy on list_accounts
  // -------------------------------------------------------------------------
  if (!useRealApi) {
    console.log("\n== Task 4: sortBy ==");
    const topByArr = await callTool(ctx, "list_accounts", {
      sortBy: "vitally.custom.arr",
      sortOrder: "desc",
      limit: 5,
      traits: ["vitally.custom.arr"],
    });
    const arrs = (topByArr.results || []).map(r => r.traits?.["vitally.custom.arr"]);
    check(
      "sortBy=ARR desc orders rows correctly",
      arrs.length >= 2 && arrs.every((v, i) => i === 0 || v <= arrs[i - 1]),
      JSON.stringify(arrs)
    );
    check(
      "first row is the highest-ARR account (Stark)",
      topByArr.results?.[0]?.name === "Stark Industries",
      topByArr.results?.[0]?.name
    );

    const topByMrrAsc = await callTool(ctx, "list_accounts", {
      sortBy: "mrr",
      sortOrder: "asc",
      limit: 5,
    });
    const mrrs = (topByMrrAsc.results || []).map(r => r.mrr);
    check(
      "sortBy=mrr asc orders rows correctly",
      mrrs.length >= 2 && mrrs.every((v, i) => i === 0 || v >= mrrs[i - 1]),
      JSON.stringify(mrrs)
    );
  }

  // -------------------------------------------------------------------------
  // P1 Task 5: filterTraits on list_accounts
  // -------------------------------------------------------------------------
  if (!useRealApi) {
    console.log("\n== Task 5: filterTraits ==");
    const tier1 = await callTool(ctx, "list_accounts", {
      filterTraits: { "vitally.custom.arrTier": "Tier 1" },
      limit: 10,
    });
    check(
      "filterTraits returns only Tier 1 accounts",
      Array.isArray(tier1.results)
        && tier1.results.length === 2
        && tier1.results.every(r => r.traits?.["vitally.custom.arrTier"] === "Tier 1"),
      `count=${tier1.results?.length}`
    );

    const combined = await callTool(ctx, "list_accounts", {
      filterTraits: { "vitally.custom.arrTier": "Tier 1" },
      sortBy: "vitally.custom.arr",
      sortOrder: "desc",
      limit: 1,
    });
    check(
      "filterTraits + sortBy + limit composes",
      combined.results?.length === 1 && combined.results[0].name === "Stark Industries",
      combined.results?.[0]?.name
    );
  }

  // -------------------------------------------------------------------------
  // P2 Task 6: aggregate_accounts
  // -------------------------------------------------------------------------
  if (!useRealApi) {
    console.log("\n== Task 6: aggregate_accounts ==");
    const sumByTier = await callTool(ctx, "aggregate_accounts", {
      groupBy: "vitally.custom.arrTier",
      metric: "sum",
      metricField: "vitally.custom.arr",
    });
    const tier1Sum = (sumByTier.rows || []).find(r => r.group === "Tier 1");
    check(
      "sum of ARR for Tier 1 = 250000 + 120000 = 370000",
      tier1Sum && tier1Sum.value === 370000,
      JSON.stringify(tier1Sum)
    );

    const totalCount = await callTool(ctx, "aggregate_accounts", {
      metric: "count",
    });
    check(
      "groupBy=null metric=count returns total population",
      totalCount.rows?.length === 1 && totalCount.rows[0].value === 5 && totalCount.rows[0].group === null,
      JSON.stringify(totalCount.rows)
    );

    const avg = await callTool(ctx, "aggregate_accounts", {
      groupBy: "vitally.custom.csmSentiment",
      metric: "avg",
      metricField: "vitally.custom.arr",
    });
    const positive = (avg.rows || []).find(r => r.group === "positive");
    check(
      "avg ARR for positive sentiment = (250000 + 120000 + 7000) / 3",
      positive && Math.abs(positive.value - (250000 + 120000 + 7000) / 3) < 0.01,
      JSON.stringify(positive)
    );

    const min = await callTool(ctx, "aggregate_accounts", {
      groupBy: "vitally.custom.arrTier",
      metric: "min",
      metricField: "vitally.custom.arr",
    });
    const tier3 = (min.rows || []).find(r => r.group === "Tier 3");
    check(
      "min ARR for Tier 3 = 7000 (Sace)",
      tier3 && tier3.value === 7000,
      JSON.stringify(tier3)
    );

    const max = await callTool(ctx, "aggregate_accounts", {
      groupBy: "vitally.custom.arrTier",
      metric: "max",
      metricField: "vitally.custom.arr",
    });
    const tier1Max = (max.rows || []).find(r => r.group === "Tier 1");
    check(
      "max ARR for Tier 1 = 250000 (Stark)",
      tier1Max && tier1Max.value === 250000
    );
  }

  // -------------------------------------------------------------------------
  // P2 Task 7: HTML strip
  // -------------------------------------------------------------------------
  if (!useRealApi) {
    console.log("\n== Task 7: HTML strip ==");
    const tasksPlain = await callTool(ctx, "get_account_tasks", { accountId: "4", limit: 10 });
    const t1 = (tasksPlain.results || []).find(t => t.id === "t1");
    check(
      "task description is plain text by default",
      t1 && !/[<>]/.test(t1.description) && t1.description.includes("[image]"),
      t1?.description
    );

    const tasksHtml = await callTool(ctx, "get_account_tasks", { accountId: "4", limit: 10, descriptionFormat: "html" });
    const t1Html = (tasksHtml.results || []).find(t => t.id === "t1");
    check(
      "descriptionFormat=html returns raw HTML",
      t1Html && t1Html.description.includes("<p>"),
      t1Html?.description
    );

    const note = await callTool(ctx, "get_note_by_id", { noteId: "n1" });
    check(
      "note body is plain text by default",
      typeof note.content === "string" && !/[<>]/.test(note.content),
      note.content
    );
  }

  // -------------------------------------------------------------------------
  // P2 Task 9: find_account_by_name forwards to search_accounts
  // -------------------------------------------------------------------------
  if (!useRealApi) {
    console.log("\n== Task 9: find_account_by_name deprecated ==");
    const found = await callTool(ctx, "find_account_by_name", { name: "Sace" });
    check(
      "find_account_by_name still works (forwarded)",
      Array.isArray(found.accounts) && found.accounts.some(a => a.name === "Sace")
    );
    check(
      "find_account_by_name carries deprecation flag",
      typeof found._deprecation === "string" && found._deprecation.includes("deprecated"),
      found._deprecation
    );
  }

  // -------------------------------------------------------------------------
  // P2 Task 10: guarded trait writes
  // -------------------------------------------------------------------------
  if (!useRealApi) {
    console.log("\n== Task 10: guarded trait writes ==");
    await expectThrow(
      callTool(ctx, "update_account", {
        accountId: "4",
        traits: { "vitally.custom.arr": 0 },
      }),
      "update_account refuses guarded trait without force",
      /guarded/i
    );

    const forced = await callTool(ctx, "update_account", {
      accountId: "4",
      force: true,
      traits: { "vitally.custom.arr": 7001 },
    });
    check(
      "update_account with force=true writes guarded trait",
      forced?.traits?.["vitally.custom.arr"] === 7001,
      JSON.stringify(forced?.traits)
    );
    // restore so other assertions about Sace ARR still pass
    await callTool(ctx, "update_account", {
      accountId: "4",
      force: true,
      traits: { "vitally.custom.arr": 7000 },
    });
  }

  // -------------------------------------------------------------------------
  // legacy demo asserts (kept from v2.0)
  // -------------------------------------------------------------------------
  if (!useRealApi) {
    console.log("\n== demo: search_accounts(name='Sace') has full traits ==");
    const sace = await callTool(ctx, "search_accounts", { name: "Sace" });
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
    const got = await callTool(ctx, "get_account", { externalId: "sace" });
    check("get_account returns Sace by externalId", got?.name === "Sace", JSON.stringify(got));
  }

  console.log("\n== list_tasks ==");
  const tasks = await callTool(ctx, "list_tasks", { limit: 5 });
  check("list_tasks returns results array", Array.isArray(tasks.results), JSON.stringify(tasks).slice(0, 200));

  ctx.child.kill();

  // -------------------------------------------------------------------------
  // P2 Task 8: workspace warnings — separate process with null scores
  // -------------------------------------------------------------------------
  if (!useRealApi) {
    console.log("\n== Task 8: workspace _warnings (separate process, null scores) ==");
    const warnCtx = spawnServer({ VITALLY_DEMO_NULL_SCORES: "1" });
    await initialize(warnCtx);
    const first = await callTool(warnCtx, "get_account", { accountId: "4" });
    check(
      "first call to get_account emits _warnings",
      Array.isArray(first._warnings)
        && first._warnings.some(w => /healthScore/.test(w))
        && first._warnings.some(w => /npsScore/.test(w)),
      JSON.stringify(first._warnings)
    );
    const second = await callTool(warnCtx, "get_account", { accountId: "4" });
    check(
      "second call does not re-emit warnings",
      !("_warnings" in second) || (Array.isArray(second._warnings) && second._warnings.length === 0)
    );
    warnCtx.child.kill();
  }

  console.log(`\n${failures.length === 0 ? "All smoke checks passed." : `${failures.length} FAILURE(S)`}`);
  process.exit(failures.length === 0 ? 0 : 1);
}

main().catch(err => {
  console.error("Smoke test crashed:", err);
  process.exit(2);
});
