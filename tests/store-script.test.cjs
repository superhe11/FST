// Run with: node --test tests/store-script.test.cjs (no npm dependencies).
const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const script = fs.readFileSync(path.join(__dirname, '../src/Fst.App/StoreScript.js'), 'utf8');

class Element {
  children = []; dataset = {}; textContent = ''; isConnected = true; listeners = {};
  constructor(tag) { this.tag = tag; }
  append(...children) { this.children.push(...children); }
  prepend(child) { this.children.unshift(child); }
  remove() { this.isConnected = false; }
  setAttribute() {}
  addEventListener(name, callback) { this.listeners[name] = callback; }
  querySelector(selector) {
    for (const child of this.children) {
      if (selector === child.tag || selector === '[data-fst-message]' && 'fstMessage' in child.dataset ||
          selector === '[data-fst-drm]' && 'fstDrm' in child.dataset) return child;
      const nested = child.querySelector(selector);
      if (nested) return nested;
    }
    return null;
  }
}
async function fixture({ pathname = '/bundle/123/', batchInstall = true, apps = ['10', '20, 30', '10'], installed = [], earlyDocument = false } = {}) {
  const host = new Element('host');
  const window = {};
  const calls = [];
  const timers = [];
  let interval;
  let currentStatus = { installed, connected: true, batchInstall };
  if (batchInstall === null) delete currentStatus.batchInstall;
  let job = { state: 'downloading', message: '' };
  const document = {
    head: new Element('head'), documentElement: new Element('html'),
    createElement: tag => new Element(tag),
    querySelector: selector => selector === '#game_area_purchase' ? host : null,
    querySelectorAll: selector => {
      if (selector === '.DRM_notice') return [];
      // Keep contents scoped so a recommended app cannot enter the queue.
      assert.equal(selector, '.bundle_package_item .tab_item[data-ds-appid], .package_landing_page_item_list .tab_item[data-ds-appid]');
      return apps.map(id => ({ getAttribute: () => id }));
    }
  };
  const context = vm.createContext({ window, document, location: { pathname },
    MutationObserver: class { observe() {} disconnect() {} },
    setTimeout: callback => timers.push(callback), clearTimeout() {},
    setInterval: callback => { interval = callback; }, clearInterval() {}
  });
  function respond() {
    const bridge = window.__FST;
    for (const [id, request] of Object.entries(bridge.requests)) {
      delete bridge.requests[id]; calls.push(request);
      bridge.responses[id] = { result: request.method === 'status' ? currentStatus : request.method === 'job' ? job : { jobId: 'batch-1' } };
    }
  }
  async function flush() {
    for (let i = 0; i < 8; i++) {
      respond();
      for (const callback of timers.splice(0)) callback();
      await Promise.resolve();
    }
  }
  if (earlyDocument) {
    document.head = null; document.documentElement = null;
    assert.doesNotThrow(() => vm.runInContext(script, context));
    assert.equal(window.__FST, undefined);
    assert.equal(window.__FSTStore, undefined);
    document.documentElement = new Element('html');
  }
  vm.runInContext(script, context);
  await flush();
  return { host, calls, window, flush,
    async refresh(update, status) { job = update; if (status) currentStatus = { ...currentStatus, ...status }; interval(); await flush(); }
  };
}

test('Linux bundle submits all unique app IDs including nested package apps; partial result permits retry', async () => {
  const f = await fixture({ installed: ['10'] });
  const button = f.host.querySelector('button');
  assert.equal(button.textContent, 'Добавить бандл в библиотеку');
  const pending = button.listeners.click();
  await f.flush(); await pending;
  const request = f.calls.find(call => call.method === 'installBatch');
  assert.deepEqual(Array.from(request.args.appids), ['20', '30']);
  await f.refresh({ state: 'partial', message: '20: missing manifest' }, { installed: ['10', '30'], busy: false });
  assert.equal(button.disabled, false);
  assert.equal(f.host.querySelector('[data-fst-message]').textContent, '20: missing manifest');
  const jobPolls = f.calls.filter(call => call.method === 'job').length;
  await f.refresh({ state: 'partial' });
  assert.equal(f.calls.filter(call => call.method === 'job').length, jobPolls);
  const retry = button.listeners.click();
  await f.flush(); await retry;
  assert.deepEqual(Array.from(f.calls.filter(call => call.method === 'installBatch').at(-1).args.appids), ['20']);
});

test('Windows and older backends do not show bundle controls', async () => {
  for (const batchInstall of [false, null]) {
    const f = await fixture({ batchInstall });
    assert.equal(f.host.querySelector('button'), null);
    assert.equal(f.calls.some(call => call.method === 'installBatch'), false);
  }
});

test('empty bundle is disabled and package pages use the batch method', async () => {
  const empty = await fixture({ apps: [] });
  assert.equal(empty.host.querySelector('button').disabled, true);
  const f = await fixture({ pathname: '/sub/7/', apps: ['10', '80'] });
  const pending = f.host.querySelector('button').listeners.click();
  await f.flush(); await pending;
  assert.deepEqual(Array.from(f.calls.find(call => call.method === 'installBatch').args.appids), ['10', '80']);
});

test('single-app install on Windows keeps the existing RPC', async () => {
  const f = await fixture({ pathname: '/app/440/', batchInstall: false });
  const pending = f.host.querySelector('button').listeners.click();
  await f.flush(); await pending;
  assert.equal(f.calls.find(call => call.method === 'install').args.appid, '440');
  assert.equal(f.calls.some(call => call.method === 'installBatch'), false);
});


test('CEF early document defers initialization and succeeds on the next injection without a head', async () => {
  const f = await fixture({ earlyDocument: true });
  assert.ok(f.window.__FSTStore);
  assert.equal(f.host.querySelector('button').textContent, 'Добавить бандл в библиотеку');
});
