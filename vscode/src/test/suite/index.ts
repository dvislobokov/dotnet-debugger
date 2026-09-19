import * as fs from 'fs';
import * as path from 'path';
import Mocha from 'mocha';

export function run(): Promise<void> {
    const mocha = new Mocha({ ui: 'tdd', color: true, timeout: 120_000 });
    for (const file of fs.readdirSync(__dirname)) {
        if (file.endsWith('.test.js')) {
            mocha.addFile(path.join(__dirname, file));
        }
    }
    return new Promise((resolve, reject) => {
        mocha.run(failures => failures ? reject(new Error(`${failures} test(s) failed`)) : resolve());
    });
}
