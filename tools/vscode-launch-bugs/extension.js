const vscode = require("vscode");
const fs = require("fs");
const path = require("path");
const { spawn } = require("child_process");

const DEFAULT_PROMPT_TEMPLATE =
  "{bug}\n\nInvestigate this bug and plan a fix. Ask the user questions if you need to with the AskUserQuestion tool. Reference @docs\\e2e-tdd-issue-debugging.md.";

const OPEN_ISSUES_PROMPT_TEMPLATE =
  "Investigate the issue described in {bug} and refine the plan (or draft one if the file only contains notes). Ask the user questions if you need to with the AskUserQuestion tool. Reference @docs\\e2e-tdd-issue-debugging.md.";

const OPEN_ISSUES_REL_DIR = path.join("docs", "plans", "open-issues");

function parsePrompts(content) {
  const prompts = [];
  let currentBlock = [];

  for (const raw of content.split("\n")) {
    const line = raw.replace(/\r$/, "");
    const dashMatch = line.match(/^\s*-\s+(.+)/);

    if (dashMatch) {
      if (currentBlock.length > 0) {
        prompts.push(currentBlock.join("\n").trim());
        currentBlock = [];
      }
      prompts.push(dashMatch[1].trim());
    } else if (line.trim() === "") {
      if (currentBlock.length > 0) {
        prompts.push(currentBlock.join("\n").trim());
        currentBlock = [];
      }
    } else {
      currentBlock.push(line);
    }
  }

  if (currentBlock.length > 0) {
    prompts.push(currentBlock.join("\n").trim());
  }

  return prompts.filter((p) => p !== "");
}

const STATE_KEY_PROMPTS_DIR = "launchBugs.lastPromptsDir";
const STATE_KEY_BUNDLE_DIR = "launchBugs.lastBundleDir";
const STATE_KEY_YAAT_SERVER_REPO_DIR = "launchBugs.yaatServerRepoDir";

const SERVER_LOG_REL_PATH = path.join("src", "Yaat.Server", "bin", "Debug", "net10.0", "yaat-server.log");
const FETCH_SERVER_LOGS_REL = path.join("tools", "fetch-server-logs.ps1");

async function collectFromPromptsFile(context) {
  const lastPromptsDir = context.globalState.get(STATE_KEY_PROMPTS_DIR);
  const fileUri = await vscode.window.showOpenDialog({
    canSelectMany: false,
    filters: { "Text files": ["txt", "md"], "All files": ["*"] },
    title: "Select prompts file",
    defaultUri: lastPromptsDir ? vscode.Uri.file(lastPromptsDir) : undefined,
  });
  if (!fileUri || fileUri.length === 0) return null;
  context.globalState.update(
    STATE_KEY_PROMPTS_DIR,
    path.dirname(fileUri[0].fsPath)
  );

  const content = fs.readFileSync(fileUri[0].fsPath, "utf-8");
  const prompts = parsePrompts(content);

  if (prompts.length === 0) {
    vscode.window.showWarningMessage("No prompts found in file.");
    return null;
  }

  return { prompts, template: DEFAULT_PROMPT_TEMPLATE };
}

async function collectFromOpenIssues() {
  const folders = vscode.workspace.workspaceFolders;
  if (!folders || folders.length === 0) {
    vscode.window.showErrorMessage(
      "No workspace folder is open. Open the yaat repo first."
    );
    return null;
  }

  const workspaceRoot = folders[0].uri.fsPath;
  const dir = path.join(workspaceRoot, OPEN_ISSUES_REL_DIR);
  if (!fs.existsSync(dir)) {
    vscode.window.showErrorMessage(
      `Folder not found: ${OPEN_ISSUES_REL_DIR} (looked in ${workspaceRoot})`
    );
    return null;
  }

  const files = fs
    .readdirSync(dir)
    .filter((f) => f.toLowerCase().endsWith(".md"))
    .sort();

  if (files.length === 0) {
    vscode.window.showWarningMessage(
      `No .md files found in ${OPEN_ISSUES_REL_DIR}.`
    );
    return null;
  }

  // Reference via workspace-relative path with forward slashes so @-refs work in claude.
  const prompts = files.map((f) =>
    "@" + path.posix.join("docs", "plans", "open-issues", f)
  );

  return { prompts, template: OPEN_ISSUES_PROMPT_TEMPLATE };
}

async function pickBundle(context) {
  const bundleChoice = await vscode.window.showQuickPick(
    ["No bundle", "Select bundle zip..."],
    { placeHolder: "Include a bug report bundle?" }
  );
  if (bundleChoice === undefined) return undefined;
  if (bundleChoice === "No bundle") return "";

  const lastBundleDir = context.globalState.get(STATE_KEY_BUNDLE_DIR);
  const bundleUri = await vscode.window.showOpenDialog({
    canSelectMany: false,
    filters: { "Zip files": ["zip"], "All files": ["*"] },
    title: "Select bundle zip",
    defaultUri: lastBundleDir ? vscode.Uri.file(lastBundleDir) : undefined,
  });
  if (!bundleUri || bundleUri.length === 0) return undefined;
  context.globalState.update(
    STATE_KEY_BUNDLE_DIR,
    path.dirname(bundleUri[0].fsPath)
  );
  return bundleUri[0].fsPath;
}

async function pickClientLog() {
  const choice = await vscode.window.showQuickPick(
    ["Yes", "No"],
    { placeHolder: "Attach client log? (%LOCALAPPDATA%/yaat/yaat-client.log)" }
  );
  if (choice !== "Yes") return "";

  const localAppData = process.env.LOCALAPPDATA;
  if (!localAppData) {
    vscode.window.showErrorMessage(
      "LOCALAPPDATA env var not set; cannot resolve client log path."
    );
    return "";
  }
  const clientLogPath = path.join(localAppData, "yaat", "yaat-client.log");
  if (!fs.existsSync(clientLogPath)) {
    vscode.window.showWarningMessage(
      `Client log not found at ${clientLogPath}; attaching anyway.`
    );
  }
  return clientLogPath;
}

async function getOrAskYaatServerRepo(context, workspaceRoot) {
  const stored = context.globalState.get(STATE_KEY_YAAT_SERVER_REPO_DIR);
  if (stored && fs.existsSync(stored)) return stored;

  const defaultPath = path.resolve(workspaceRoot, "..", "yaat-server");
  const picked = await vscode.window.showOpenDialog({
    canSelectFolders: true,
    canSelectFiles: false,
    canSelectMany: false,
    title: "Select yaat-server repo folder",
    defaultUri: vscode.Uri.file(defaultPath),
  });
  if (!picked || picked.length === 0) return undefined;

  const repoDir = picked[0].fsPath;
  context.globalState.update(STATE_KEY_YAAT_SERVER_REPO_DIR, repoDir);
  return repoDir;
}

function fetchYaat1ServerLog(workspaceRoot, minutes) {
  const scriptPath = path.join(workspaceRoot, FETCH_SERVER_LOGS_REL);

  return vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Notification,
      cancellable: true,
      title: minutes === 0
        ? "Fetching YAAT1 server logs (all since boot)..."
        : `Fetching YAAT1 server logs (last ${minutes} min)...`,
    },
    (_progress, token) =>
      new Promise((resolve) => {
        const child = spawn(
          "pwsh",
          ["-NoProfile", "-File", scriptPath, "-Minutes", String(minutes)],
          { cwd: workspaceRoot, windowsHide: true }
        );

        let stdout = "";
        let stderr = "";
        child.stdout.on("data", (d) => { stdout += d.toString(); });
        child.stderr.on("data", (d) => { stderr += d.toString(); });

        token.onCancellationRequested(() => {
          child.kill();
          resolve({ ok: false, error: "Cancelled by user." });
        });

        child.on("error", (err) => {
          resolve({ ok: false, error: err.message });
        });

        child.on("close", (code) => {
          const combined = (stdout + "\n" + stderr).replace(/\x1b\[[0-9;]*m/g, "");
          if (code !== 0) {
            const summary = stderr.trim() || stdout.trim() || `exit code ${code}`;
            resolve({ ok: false, error: summary.split("\n").slice(-5).join(" | ") });
            return;
          }
          const match = combined.match(/Saved \d+ lines to (.+?)\s*$/m);
          if (!match) {
            resolve({ ok: false, error: "Could not parse output path from script." });
            return;
          }
          resolve({ ok: true, path: match[1].trim() });
        });
      })
  );
}

async function pickServerLog(context, workspaceRoot) {
  while (true) {
    const choice = await vscode.window.showQuickPick(
      [
        { label: "Local server", description: "Read yaat-server.log from a local yaat-server repo" },
        { label: "YAAT1 (production)", description: "Fetch recent docker logs from the production droplet" },
        { label: "None", description: "Skip server logs" },
      ],
      { placeHolder: "Select server-log source" }
    );
    if (!choice || choice.label === "None") return null;

    if (!workspaceRoot) {
      vscode.window.showErrorMessage(
        "No workspace folder is open; cannot resolve server logs."
      );
      return null;
    }

    if (choice.label === "Local server") {
      const repoDir = await getOrAskYaatServerRepo(context, workspaceRoot);
      if (!repoDir) return null;
      const logPath = path.join(repoDir, SERVER_LOG_REL_PATH);
      if (!fs.existsSync(logPath)) {
        vscode.window.showWarningMessage(
          `Local server log not found at ${logPath}; attaching anyway.`
        );
      }
      return { kind: "local", path: logPath };
    }

    if (choice.label === "YAAT1 (production)") {
      const windowChoice = await vscode.window.showQuickPick(
        [
          { label: "All logs since boot", description: "Fetch all available container logs (default)" },
          { label: "Last N minutes...", description: "Fetch only the most recent N minutes" },
        ],
        { placeHolder: "How much history to fetch?" }
      );
      if (!windowChoice) return null;

      let minutes = 0;
      if (windowChoice.label === "Last N minutes...") {
        const minutesStr = await vscode.window.showInputBox({
          prompt: "Minutes of logs to fetch",
          value: "60",
          validateInput: (s) => (/^[1-9]\d*$/.test(s) ? null : "Enter a positive integer"),
        });
        if (minutesStr === undefined) return null;
        minutes = parseInt(minutesStr, 10);
      }

      const result = await fetchYaat1ServerLog(workspaceRoot, minutes);
      if (result.ok) return { kind: "yaat1", path: result.path, minutes };

      const action = await vscode.window.showErrorMessage(
        `YAAT1 fetch failed: ${result.error}`,
        "Retry",
        "Skip"
      );
      if (action === "Retry") continue;
      return null;
    }
  }
}

function launchTerminals(prompts, template, bundleInput, clientLog, serverLog) {
  const trailingLines = [];
  if (bundleInput) trailingLines.push(`Bug report bundle: ${bundleInput}`);
  if (clientLog) trailingLines.push(`Client log: ${clientLog}`);
  if (serverLog) {
    let tag;
    if (serverLog.kind === "local") {
      tag = "local";
    } else {
      tag = serverLog.minutes === 0 ? "YAAT1, all since boot" : `YAAT1, last ${serverLog.minutes} min`;
    }
    trailingLines.push(`Server log (${tag}): ${serverLog.path}`);
  }
  const trailingBlock = trailingLines.length > 0 ? `\n\n${trailingLines.join("\n")}` : "";
  const launchInterval = 3000;
  const startupDelay = 6000;
  const shiftTab = "\x1b[Z";

  for (let i = 0; i < prompts.length; i++) {
    const fullPrompt = template.replace(/\{bug\}/g, prompts[i]) + trailingBlock;
    const launchAt = i * launchInterval;

    setTimeout(() => {
      const terminal = vscode.window.createTerminal();

      // Start claude (needs a TTY — can't pipe into it)
      terminal.sendText(`claude --dangerously-skip-permissions`);

      // After claude starts: Shift+Tab x4 to enter plan mode, type prompt, Enter
      setTimeout(() => {
        terminal.sendText(shiftTab, false);
        setTimeout(() => {
          terminal.sendText(shiftTab, false);
          setTimeout(() => {
            terminal.sendText(shiftTab, false);
            setTimeout(() => {
              terminal.sendText(shiftTab, false);
              setTimeout(() => {
                terminal.sendText(fullPrompt, false);
                setTimeout(() => {
                  terminal.sendText("", true);
                }, 500);
              }, 500);
            }, 200);
          }, 200);
        }, 200);
      }, startupDelay);
    }, launchAt);
  }
}

function activate(context) {
  context.subscriptions.push(
    vscode.commands.registerCommand("launchBugs.run", async () => {
      const mode = await vscode.window.showQuickPick(
        [
          {
            label: "From prompts file",
            description: "Read bug descriptions from a .txt/.md file",
            value: "file",
          },
          {
            label: "From open-issues folder",
            description: `One terminal per .md file in ${OPEN_ISSUES_REL_DIR}`,
            value: "openIssues",
          },
        ],
        { placeHolder: "Select source for bug prompts" }
      );
      if (!mode) return;

      const collected =
        mode.value === "file"
          ? await collectFromPromptsFile(context)
          : await collectFromOpenIssues();
      if (!collected) return;

      const bundleInput = await pickBundle(context);
      if (bundleInput === undefined) return;

      let clientLog = "";
      let serverLog = null;
      if (bundleInput === "") {
        const folders = vscode.workspace.workspaceFolders;
        const workspaceRoot = folders && folders.length > 0 ? folders[0].uri.fsPath : null;
        clientLog = await pickClientLog();
        serverLog = await pickServerLog(context, workspaceRoot);
      }

      const previews = collected.prompts.map((p, i) => {
        const first = p.split("\n")[0];
        const label = first.length > 80 ? first.substring(0, 77) + "..." : first;
        return `[${i + 1}] ${label}`;
      });

      const confirm = await vscode.window.showInformationMessage(
        `Launch ${collected.prompts.length} bug terminals?`,
        { detail: previews.join("\n"), modal: true },
        "Launch"
      );
      if (confirm !== "Launch") return;

      launchTerminals(collected.prompts, collected.template, bundleInput, clientLog, serverLog);

      vscode.window.showInformationMessage(
        `Launched ${collected.prompts.length} bug terminals.`
      );
    })
  );
}

module.exports = { activate };
