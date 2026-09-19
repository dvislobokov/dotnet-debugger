// Copies a published adapter (artifacts/publish/<rid>) into ./adapter so it gets bundled into the .vsix.
//   node scripts/copy-adapter.js [rid]
const fs = require('fs');
const path = require('path');

const defaultRid = { win32: 'win', linux: 'linux', darwin: 'osx' }[process.platform] + '-' + (process.arch === 'arm64' ? 'arm64' : 'x64');
const rid = process.argv[2] || defaultRid;
const source = path.join(__dirname, '..', '..', 'artifacts', 'publish', rid);
const target = path.join(__dirname, '..', 'adapter');

if (!fs.existsSync(source)) {
  console.error(`${source} does not exist. Run build/publish.ps1 (or publish.sh) -Rid ${rid} first.`);
  process.exit(1);
}
fs.rmSync(target, { recursive: true, force: true });
fs.cpSync(source, target, { recursive: true });
console.log(`Bundled adapter for ${rid}`);
