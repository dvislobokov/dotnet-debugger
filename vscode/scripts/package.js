// Builds a platform specific .vsix:  node scripts/package.js [rid]
// The adapter is a native-dependent .NET application, so every platform gets its own package (vsce --target).
const { execFileSync } = require('child_process');
const path = require('path');

const defaultRid = { win32: 'win', linux: 'linux', darwin: 'osx' }[process.platform] + '-' + (process.arch === 'arm64' ? 'arm64' : 'x64');
const rid = process.argv[2] || defaultRid;
const targets = {
  'win-x64': 'win32-x64', 'win-arm64': 'win32-arm64',
  'linux-x64': 'linux-x64', 'linux-arm64': 'linux-arm64', 'linux-musl-x64': 'alpine-x64', 'linux-musl-arm64': 'alpine-arm64',
  'osx-x64': 'darwin-x64', 'osx-arm64': 'darwin-arm64',
};
const target = targets[rid];
if (!target) {
  console.error(`Unknown runtime identifier '${rid}'. Known: ${Object.keys(targets).join(', ')}`);
  process.exit(1);
}

const run = (file, args) => execFileSync(file, args, { stdio: 'inherit', cwd: path.join(__dirname, '..'), shell: process.platform === 'win32' });
run('npx', ['tsc', '-p', '.']);
run('node', ['scripts/copy-adapter.js', rid]);
run('npx', ['vsce', 'package', '--target', target, '--no-dependencies']);
