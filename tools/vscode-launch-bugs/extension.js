const vscode = require("vscode");
const fs = require("fs");
const path = require("path");

const DEFAULT_PROMPT_TEMPLATE =
  "{bug}\n\nInvestigate this bug and plan a fix. Ask the user questions if you need to with the AskUserQuestion tool. Reference @docs\\e2e-tdd-issue-debugging.md.";

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

function activate(context) {
  context.subscriptions.push(
    vscode.commands.registerCommand("launchBugs.run", async () => {
      const lastPromptsDir = context.globalState.get(STATE_KEY_PROMPTS_DIR);
      const fileUri = await vscode.window.showOpenDialog({
        canSelectMany: false,
        filters: { "Text files": ["txt", "md"], "All files": ["*"] },
        title: "Select prompts file",
        defaultUri: lastPromptsDir
          ? vscode.Uri.file(lastPromptsDir)
          : undefined,
      });
      if (!fileUri || fileUri.length === 0) return;
      context.globalState.update(
        STATE_KEY_PROMPTS_DIR,
        path.dirname(fileUri[0].fsPath)
      );

      const content = fs.readFileSync(fileUri[0].fsPath, "utf-8");
      const prompts = parsePrompts(content);

      if (prompts.length === 0) {
        vscode.window.showWarningMessage("No prompts found in file.");
        return;
      }

      const bundleChoice = await vscode.window.showQuickPick(
        ["No bundle", "Select bundle zip..."],
        { placeHolder: "Include a bug report bundle?" }
      );
      if (bundleChoice === undefined) return;

      let bundleInput = "";
      if (bundleChoice === "Select bundle zip...") {
        const lastBundleDir = context.globalState.get(STATE_KEY_BUNDLE_DIR);
        const bundleUri = await vscode.window.showOpenDialog({
          canSelectMany: false,
          filters: { "Zip files": ["zip"], "All files": ["*"] },
          title: "Select bundle zip",
          defaultUri: lastBundleDir
            ? vscode.Uri.file(lastBundleDir)
            : undefined,
        });
        if (!bundleUri || bundleUri.length === 0) return;
        context.globalState.update(
          STATE_KEY_BUNDLE_DIR,
          path.dirname(bundleUri[0].fsPath)
        );
        bundleInput = bundleUri[0].fsPath;
      }

      const template = DEFAULT_PROMPT_TEMPLATE;

      const previews = prompts.map((p, i) => {
        const first = p.split("\n")[0];
        const label = first.length > 80 ? first.substring(0, 77) + "..." : first;
        return `[${i + 1}] ${label}`;
      });

      const confirm = await vscode.window.showInformationMessage(
        `Launch ${prompts.length} bug terminals?`,
        { detail: previews.join("\n"), modal: true },
        "Launch"
      );
      if (confirm !== "Launch") return;

      const bundleLine = bundleInput
        ? `\n\nBug report bundle: ${bundleInput}`
        : "";
      const launchInterval = 3000;
      const startupDelay = 6000;
      const shiftTab = "\x1b[Z";

      for (let i = 0; i < prompts.length; i++) {
        const fullPrompt =
          template.replace(/\{bug\}/g, prompts[i]) + bundleLine;
        const launchAt = i * launchInterval;

        setTimeout(() => {
          const terminal = vscode.window.createTerminal({
            name: `Bug ${i + 1}`,
          });

          // Start claude (needs a TTY — can't pipe into it)
          terminal.sendText(`claude --dangerously-skip-permissions`);

          // After claude starts: Shift+Tab x3 to enter plan mode, type prompt, Enter
          const inputDelay = startupDelay;
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
          }, inputDelay);
        }, launchAt);
      }

      vscode.window.showInformationMessage(
        `Launched ${prompts.length} bug terminals.`
      );
    })
  );
}

module.exports = { activate };
