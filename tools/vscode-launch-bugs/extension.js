const vscode = require("vscode");
const fs = require("fs");
const path = require("path");

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

function launchTerminals(prompts, template, bundleInput) {
  const bundleLine = bundleInput ? `\n\nBug report bundle: ${bundleInput}` : "";
  const launchInterval = 3000;
  const startupDelay = 6000;
  const shiftTab = "\x1b[Z";

  for (let i = 0; i < prompts.length; i++) {
    const fullPrompt = template.replace(/\{bug\}/g, prompts[i]) + bundleLine;
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

      launchTerminals(collected.prompts, collected.template, bundleInput);

      vscode.window.showInformationMessage(
        `Launched ${collected.prompts.length} bug terminals.`
      );
    })
  );
}

module.exports = { activate };
