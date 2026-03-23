const vscode = require("vscode");
const fs = require("fs");
const path = require("path");
const { execSync } = require("child_process");
const crypto = require("crypto");

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

/**
 * Finds the yaat and yaat-server repo roots from the VS Code workspace.
 * Assumes the first workspace folder is the yaat repo and yaat-server is a sibling.
 */
function getRepoRoots() {
  const folders = vscode.workspace.workspaceFolders;
  if (!folders || folders.length === 0) {
    throw new Error("No workspace folder open");
  }
  const yaatRoot = folders[0].uri.fsPath;
  const serverRoot = path.join(path.dirname(yaatRoot), "yaat-server");
  if (!fs.existsSync(path.join(serverRoot, ".git"))) {
    throw new Error(`yaat-server repo not found at ${serverRoot}`);
  }
  return { yaatRoot, serverRoot };
}

/**
 * Creates paired worktrees for yaat and yaat-server as siblings under
 * .claude/worktrees/<name>/, preserving the ../yaat reference that
 * yaat-server's Directory.Build.props relies on.
 *
 * Returns the yaat worktree path (the one claude should run from).
 */
function createPairedWorktree(yaatRoot, serverRoot, name) {
  const base = path.join(yaatRoot, ".claude", "worktrees", name);
  const yaatWt = path.join(base, "yaat");
  const serverWt = path.join(base, "yaat-server");
  const branch = `worktree-${name}`;

  // All inputs are internally generated (hex strings, workspace paths).
  // No user-supplied text reaches these commands.
  execSync(`git worktree add "${yaatWt}" -b "${branch}"`, {
    cwd: yaatRoot,
    stdio: "pipe",
  });
  execSync(`git worktree add "${serverWt}" -b "${branch}"`, {
    cwd: serverRoot,
    stdio: "pipe",
  });

  return yaatWt;
}

/**
 * Lists existing paired worktree names under .claude/worktrees/.
 */
function listWorktreeNames(yaatRoot) {
  const wtDir = path.join(yaatRoot, ".claude", "worktrees");
  if (!fs.existsSync(wtDir)) return [];
  return fs.readdirSync(wtDir).filter((name) => {
    const yaatWt = path.join(wtDir, name, "yaat");
    const serverWt = path.join(wtDir, name, "yaat-server");
    return fs.existsSync(yaatWt) || fs.existsSync(serverWt);
  });
}

/**
 * Removes a paired worktree and its branches from both repos.
 */
function removePairedWorktree(yaatRoot, serverRoot, name) {
  const base = path.join(yaatRoot, ".claude", "worktrees", name);
  const yaatWt = path.join(base, "yaat");
  const serverWt = path.join(base, "yaat-server");
  const branch = `worktree-${name}`;

  for (const [wt, cwd] of [
    [serverWt, serverRoot],
    [yaatWt, yaatRoot],
  ]) {
    try {
      execSync(`git worktree remove "${wt}" --force`, {
        cwd,
        stdio: "pipe",
      });
    } catch {
      // Worktree may already be removed or path deleted manually
    }
  }

  for (const cwd of [yaatRoot, serverRoot]) {
    try {
      execSync(`git branch -D "${branch}"`, { cwd, stdio: "pipe" });
    } catch {
      // Branch may not exist
    }
  }

  try {
    fs.rmSync(base, { recursive: true });
  } catch {
    // Directory may already be gone
  }
}

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

      const worktreeChoice = await vscode.window.showQuickPick(
        [
          "Yes \u2014 isolated worktree per bug (Recommended)",
          "No \u2014 all in main working tree",
        ],
        { placeHolder: "Use paired worktrees (yaat + yaat-server)?" }
      );
      if (worktreeChoice === undefined) return;
      const useWorktrees = worktreeChoice.startsWith("Yes");

      let repoRoots;
      if (useWorktrees) {
        try {
          repoRoots = getRepoRoots();
        } catch (err) {
          vscode.window.showErrorMessage(
            `Cannot create worktrees: ${err.message}`
          );
          return;
        }
      }

      const template = DEFAULT_PROMPT_TEMPLATE;

      const previews = prompts.map((p, i) => {
        const first = p.split("\n")[0];
        const label =
          first.length > 80 ? first.substring(0, 77) + "..." : first;
        return `[${i + 1}] ${label}`;
      });

      const confirm = await vscode.window.showInformationMessage(
        `Launch ${prompts.length} bug terminals${useWorktrees ? " with worktrees" : ""}?`,
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

      // Pre-create all worktrees synchronously before launching terminals
      const worktreePaths = [];
      if (useWorktrees) {
        await vscode.window.withProgress(
          {
            location: vscode.ProgressLocation.Notification,
            title: "Creating worktrees",
            cancellable: false,
          },
          async (progress) => {
            for (let i = 0; i < prompts.length; i++) {
              const name = `bug-${crypto.randomBytes(4).toString("hex")}`;
              progress.report({
                message: `${i + 1}/${prompts.length}`,
                increment: 100 / prompts.length,
              });
              try {
                const yaatWt = createPairedWorktree(
                  repoRoots.yaatRoot,
                  repoRoots.serverRoot,
                  name
                );
                worktreePaths.push(yaatWt);
              } catch (err) {
                vscode.window.showErrorMessage(
                  `Failed to create worktree ${i + 1}: ${err.message}`
                );
                worktreePaths.push(null);
              }
            }
          }
        );
      }

      for (let i = 0; i < prompts.length; i++) {
        const fullPrompt =
          template.replace(/\{bug\}/g, prompts[i]) + bundleLine;
        const launchAt = i * launchInterval;
        const cwd =
          useWorktrees && worktreePaths[i] ? worktreePaths[i] : undefined;

        setTimeout(() => {
          const terminal = vscode.window.createTerminal({
            name: `Bug ${i + 1}`,
            cwd,
          });

          // Start claude (needs a TTY — can't pipe into it)
          terminal.sendText(`claude --dangerously-skip-permissions --worktree`);

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
        `Launched ${prompts.length} bug terminals${useWorktrees ? " with worktrees" : ""}.`
      );
    })
  );

  context.subscriptions.push(
    vscode.commands.registerCommand("launchBugs.cleanup", async () => {
      let repoRoots;
      try {
        repoRoots = getRepoRoots();
      } catch (err) {
        vscode.window.showErrorMessage(err.message);
        return;
      }

      const names = listWorktreeNames(repoRoots.yaatRoot);
      if (names.length === 0) {
        vscode.window.showInformationMessage("No bug worktrees found.");
        return;
      }

      const selected = await vscode.window.showQuickPick(
        ["Remove all", ...names],
        {
          placeHolder: `Found ${names.length} worktree(s). Select which to remove.`,
          canPickMany: false,
        }
      );
      if (!selected) return;

      const toRemove = selected === "Remove all" ? names : [selected];

      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: "Removing worktrees",
          cancellable: false,
        },
        async (progress) => {
          for (let i = 0; i < toRemove.length; i++) {
            progress.report({
              message: `${i + 1}/${toRemove.length}`,
              increment: 100 / toRemove.length,
            });
            removePairedWorktree(
              repoRoots.yaatRoot,
              repoRoots.serverRoot,
              toRemove[i]
            );
          }
        }
      );

      vscode.window.showInformationMessage(
        `Removed ${toRemove.length} worktree(s).`
      );
    })
  );
}

module.exports = { activate };
