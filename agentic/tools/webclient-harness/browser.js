// Locates a browser already installed on the machine, for the harnesses that need real layout.
//
// Nothing is downloaded: `playwright-core` drives an existing Edge or Chrome. The lookup prefers
// what the operator names, then whatever is on PATH, and only then falls back to the standard
// install locations for each platform. Those fallbacks are the vendors' own default paths, identical
// on every machine of that OS, and are not read from or specific to this one.
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const PATH_NAMES = process.platform === 'win32'
    ? ['msedge.exe', 'chrome.exe']
    : ['microsoft-edge', 'google-chrome', 'chromium', 'chromium-browser'];

const DEFAULT_LOCATIONS = {
    win32: [
        'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
        'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
        'C:/Program Files/Google/Chrome/Application/chrome.exe',
        'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe'
    ],
    darwin: [
        '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
        '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'
    ],
    linux: [
        '/usr/bin/microsoft-edge',
        '/usr/bin/google-chrome',
        '/usr/bin/chromium',
        '/usr/bin/chromium-browser'
    ]
};

function exists(candidate) {
    try {
        return !!candidate && fs.existsSync(candidate);
    } catch (err) {
        return false;
    }
}

function fromPath() {
    const locator = process.platform === 'win32' ? 'where' : 'which';

    for (const name of PATH_NAMES) {
        try {
            const found = execFileSync(locator, [name], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] })
                .split(/\r?\n/)
                .map((line) => line.trim())
                .filter(Boolean)[0];
            if (exists(found)) {
                return found;
            }
        } catch (err) {
            // Not on PATH under that name; try the next.
        }
    }

    return null;
}

/**
 * Returns the path to a usable browser, or null when none is installed.
 */
function resolveBrowser() {
    if (process.env.STM_BROWSER) {
        // Named explicitly: fail loudly rather than silently using something else.
        if (!exists(process.env.STM_BROWSER)) {
            throw new Error('STM_BROWSER is set but does not exist: ' + process.env.STM_BROWSER);
        }

        return path.resolve(process.env.STM_BROWSER);
    }

    return fromPath() || (DEFAULT_LOCATIONS[process.platform] || []).find(exists) || null;
}

/**
 * Resolves a browser or exits 2 with an explanation, which is what both harnesses want.
 */
function requireBrowser() {
    let browser = null;
    try {
        browser = resolveBrowser();
    } catch (err) {
        console.error(err.message);
        process.exit(2);
    }

    if (!browser) {
        console.error('No Edge or Chrome found. Install one, or set STM_BROWSER to a browser executable.');
        process.exit(2);
    }

    return browser;
}

module.exports = { resolveBrowser, requireBrowser };
