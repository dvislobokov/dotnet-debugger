import * as path from 'path';
import { runTests } from '@vscode/test-electron';

// Runs the integration tests inside a real (downloaded) VS Code with tests/TestApp opened as the workspace.
async function main(): Promise<void> {
    const extensionDevelopmentPath = path.resolve(__dirname, '..', '..');
    const workspace = path.resolve(extensionDevelopmentPath, '..', 'tests', 'TestApp');
    await runTests({
        extensionDevelopmentPath,
        extensionTestsPath: path.resolve(__dirname, 'suite', 'index'),
        launchArgs: [workspace, '--disable-extensions', '--disable-workspace-trust'],
    });
}

main().catch(error => {
    console.error(error);
    process.exit(1);
});
