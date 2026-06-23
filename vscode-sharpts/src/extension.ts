/**
 * SharpTS VS Code extension.
 *
 * Thin client over the SharpTS language server (`sharpts lsp`): all language
 * features (diagnostics, and future hover/completion) come from the server via
 * vscode-languageclient. The extension only spawns the server and keeps the
 * compile/run commands, which are build operations rather than LSP concerns.
 */

import * as path from 'path';
import * as vscode from 'vscode';
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
    TransportKind
} from 'vscode-languageclient/node';
import { CompileCommands } from './commands/CompileCommands';

let client: LanguageClient | undefined;

export async function activate(context: vscode.ExtensionContext) {
    const config = vscode.workspace.getConfiguration('sharpts');

    // Bundled server DLL, run as `dotnet <SharpTS.dll> lsp`.
    const serverDll = path.join(context.extensionPath, 'bin', 'server', 'SharpTS.dll');
    const dotnetPath = config.get<string>('dotnetPath') || 'dotnet';

    const args = [serverDll, 'lsp'];
    const projectFile = config.get<string>('projectFile');
    if (projectFile) {
        args.push('--project', projectFile);
    }
    for (const ref of config.get<string[]>('additionalReferences', [])) {
        args.push('-r', ref);
    }

    const serverOptions: ServerOptions = {
        run: { command: dotnetPath, args, transport: TransportKind.stdio },
        debug: { command: dotnetPath, args, transport: TransportKind.stdio }
    };

    const clientOptions: LanguageClientOptions = {
        documentSelector: [
            { language: 'typescript', scheme: 'file' },
            { language: 'typescriptreact', scheme: 'file' }
        ],
        synchronize: {
            configurationSection: 'sharpts'
        }
    };

    client = new LanguageClient('sharpts', 'SharpTS Language Server', serverOptions, clientOptions);
    await client.start();

    // Compile / run commands (build operations, not LSP).
    const compileCommands = new CompileCommands(serverDll);
    context.subscriptions.push(
        vscode.commands.registerCommand('sharpts.compile', () => compileCommands.compile()),
        vscode.commands.registerCommand('sharpts.run', () => compileCommands.run()),
        vscode.commands.registerCommand('sharpts.compileAndRun', () => compileCommands.compileAndRun()),
        vscode.commands.registerCommand('sharpts.restartServer', () => client?.restart()),
        { dispose: () => compileCommands.dispose() }
    );
}

export async function deactivate() {
    await client?.stop();
}
