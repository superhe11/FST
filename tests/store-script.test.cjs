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
  append(...children) { for (const child of children) child.parent = this; this.children.push(...children); }
  prepend(child) { child.parent = this; this.children.unshift(child); }
  remove() {
    this.isConnected = false;
    if (this.parent) this.parent.children = this.parent.children.filter(child => child !== this);
  }
  setAttribute() {}
  addEventListener(name, callback) { this.listeners[name] = callback; }
  querySelector(selector) {
    for (const child of this.children) {
      if (selector === child.tag || selector === '[data-fst-message]' && 'fstMessage' in child.dataset ||
          selector === '[data-fst-drm]' && 'fstDrm' in child.dataset ||
          selector === '[data-fst-cancel]' && 'fstCancel' in child.dataset) return child;
      const nested = child.querySelector(selector);
      if (nested) return nested;
    }
    return null;
  }
}
async function fixture({ pathname = '/bundle/123/', batchInstall = true, batchCancel = true, apps = ['10', '20, 30', '10'], installed = [], earlyDocument = false } = {}) {
  const host = new Element('host');
  const window = {};
  const calls = [];
  const timers = [];
  let interval;
  let currentStatus = { installed, connected: true, batchInstall, batchCancel };
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
  const location = { pathname };
  const context = vm.createContext({ window, document, location,
    MutationObserver: class { observe() {} disconnect() {} },
    setTimeout: callback => timers.push(callback), clearTimeout() {},
    setInterval: callback => { interval = callback; }, clearInterval() {}
  });
  function respond() {
    const bridge = window.__FST;
    for (const [id, request] of Object.entries(bridge.requests)) {
      delete bridge.requests[id]; calls.push(request);
      if (request.method === 'cancelBatch') job = { ...job, canCancel: false, cancellationRequested: true };
      bridge.responses[id] = { result: request.method === 'status' ? currentStatus : request.method === 'job' ? job : request.method === 'cancelBatch' ? { cancellationRequested: true } : { jobId: 'batch-1' } };
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
    async refresh(update, status) { job = update; if (status) currentStatus = { ...currentStatus, ...status }; interval(); await flush(); },
    async navigate(pathname) { location.pathname = pathname; interval(); await flush(); }
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


test('batch progress is rendered, cancellation is requested once and cancelled jobs stop polling', async () => {
  const f = await fixture();
  const pending = f.host.querySelector('button').listeners.click();
  await f.flush(); await pending;
  const message = 'Приложение 1 из 3 · ожидание ответа Ryuu · прошло 40 с';
  await f.refresh({ state: 'downloading', message, canCancel: true });
  const cancel = f.host.querySelector('[data-fst-cancel]');
  assert.equal(cancel.hidden, false);
  assert.equal(cancel.disabled, false);
  assert.equal(f.host.querySelector('[data-fst-message]').textContent, message);
  const cancelling = cancel.listeners.click();
  assert.equal(cancel.disabled, true);
  await cancel.listeners.click();
  await f.flush(); await cancelling;
  assert.equal(cancel.disabled, true);
  assert.equal(cancel.textContent, 'Отмена…');
  assert.equal(f.calls.filter(c => c.method === 'cancelBatch').length, 1);
  assert.equal(f.calls.find(c => c.method === 'cancelBatch').args.jobId, 'batch-1');
  await f.refresh({ state: 'cancelled', message: 'Бандл отменён.', canCancel: false }, { busy: false });
  assert.equal(cancel.hidden, true);
  assert.equal(f.host.querySelector('button').disabled, false);
  const count = f.calls.filter(c => c.method === 'job').length;
  await f.refresh({ state: 'cancelled' });
  assert.equal(f.calls.filter(c => c.method === 'job').length, count);
});

test('reload can reconnect to an active batch using status; old backend never offers cancel', async () => {
  const f = await fixture();
  await f.refresh({ state: 'downloading', message: 'still working', canCancel: true }, { activeBatchJobId: 'batch-running', busy: true });
  assert.equal(f.calls.find(c => c.method === 'job').args.jobId, 'batch-running');
  assert.equal(f.host.querySelector('[data-fst-cancel]').hidden, false);
  const old = await fixture({ batchCancel: false });
  await old.refresh({ state: 'downloading', canCancel: true }, { activeBatchJobId: 'old-job', busy: true });
  assert.equal(old.host.querySelector('[data-fst-cancel]').hidden, true);
});


for (const pathname of ['/app/999/', '/bundle/456/', '/sub/789/']) {
  test(`recovered global batch cannot mark an unrelated page installed: ${pathname}`, async () => {
    const f = await fixture({ pathname, apps: ['999', '1000'] });
    await f.refresh({ state: 'downloading', message: 'Global batch progress', canCancel: true },
      { activeBatchJobId: 'other-batch', busy: true });
    assert.equal(f.host.querySelector('button').disabled, true);
    assert.equal(f.host.querySelector('[data-fst-message]').textContent, 'Global batch progress');
    assert.equal(f.host.querySelector('[data-fst-cancel]').hidden, false);
    await f.refresh({ state: 'complete', message: 'Other batch complete', items: [{ appId: '10', installed: true }] },
      { activeBatchJobId: null, busy: false, installed: ['10'] });
    const button = f.host.querySelector('button');
    assert.equal(button.disabled, false);
    assert.equal(button.textContent, pathname.startsWith('/app/') ? 'Бесплатно добавить в библиотеку' : 'Добавить бандл в библиотеку');
    assert.equal(f.host.querySelector('[data-fst-message]').hidden, true);
    assert.equal(f.host.querySelector('[data-fst-cancel]').hidden, true);
    const polls = f.calls.filter(call => call.method === 'job').length;
    await f.refresh({ state: 'complete' });
    assert.equal(f.calls.filter(call => call.method === 'job').length, polls);
    const pending = button.listeners.click();
    await f.flush(); await pending;
    const request = f.calls.find(call => call.method === (pathname.startsWith('/app/') ? 'install' : 'installBatch'));
    if (pathname.startsWith('/app/')) assert.equal(request.args.appid, '999');
    else assert.deepEqual(Array.from(request.args.appids), ['999', '1000']);
  });
}

test('navigation during batch submission preserves global progress without adopting the destination page', async () => {
  const f = await fixture();
  const pending = f.host.querySelector('button').listeners.click();
  await f.navigate('/app/999/');
  await pending;
  await f.refresh({ state: 'downloading', message: 'Batch still running', canCancel: true },
    { activeBatchJobId: 'batch-1', busy: true });
  assert.equal(f.host.querySelector('[data-fst-message]').textContent, 'Batch still running');
  const cancel = f.host.querySelector('[data-fst-cancel]');
  assert.equal(cancel.hidden, false);
  const cancelling = cancel.listeners.click();
  await f.flush(); await cancelling;
  assert.equal(f.calls.find(call => call.method === 'cancelBatch').args.jobId, 'batch-1');
  await f.refresh({ state: 'complete', message: 'Bundle complete' },
    { activeBatchJobId: null, busy: false, installed: ['10', '20', '30'] });
  assert.equal(f.host.querySelector('button').disabled, false);
  assert.equal(f.host.querySelector('button').textContent, 'Бесплатно добавить в библиотеку');
  assert.equal(f.host.querySelector('[data-fst-message]').hidden, true);
});

test('completion on the originating page still works before the installed catalog refreshes', async () => {
  const f = await fixture({ pathname: '/app/999/', batchInstall: false });
  const pending = f.host.querySelector('button').listeners.click();
  await f.flush(); await pending;
  await f.refresh({ state: 'complete', message: 'Added successfully' }, { busy: false });
  assert.equal(f.host.querySelector('button').disabled, true);
  assert.equal(f.host.querySelector('button').textContent, 'Добавлено в библиотеку');
  assert.equal(f.host.querySelector('[data-fst-message]').textContent, 'Added successfully');
});

test('recovered completion uses installed catalog to recognize the current app', async () => {
  const f = await fixture({ pathname: '/app/999/' });
  await f.refresh({ state: 'downloading', message: 'Batch progress' }, { activeBatchJobId: 'recovered', busy: true });
  await f.refresh({ state: 'complete', message: 'Batch complete' }, { activeBatchJobId: null, busy: false, installed: ['999'] });
  assert.equal(f.host.querySelector('button').disabled, true);
  assert.equal(f.host.querySelector('button').textContent, 'Добавлено в библиотеку');
});

test('originating bundle keeps its completed result while active job ID lingers during cleanup', async () => {
  const f = await fixture();
  const pending = f.host.querySelector('button').listeners.click();
  await f.flush(); await pending;
  await f.refresh({ state: 'complete', message: 'This bundle completed' }, { activeBatchJobId: 'batch-1', busy: true });
  const polls = f.calls.filter(call => call.method === 'job').length;
  await f.refresh({ state: 'complete' });
  assert.equal(f.calls.filter(call => call.method === 'job').length, polls);
  assert.equal(f.host.querySelector('button').textContent, 'Добавлено в библиотеку');
  assert.equal(f.host.querySelector('[data-fst-message]').textContent, 'This bundle completed');
  assert.equal(f.host.querySelector('[data-fst-cancel]').hidden, true);
});

for (const state of ['partial', 'failed', 'cancelled']) {
  test(`foreign batch ${state} is not presented as the current game's result`, async () => {
    const f = await fixture({ pathname: '/app/999/' });
    await f.refresh({ state: 'downloading', message: 'Global progress' }, { activeBatchJobId: 'other-batch', busy: true });
    await f.refresh({ state, message: 'Result for another batch' }, { activeBatchJobId: null, busy: false });
    assert.equal(f.host.querySelector('button').disabled, false);
    assert.equal(f.host.querySelector('[data-fst-message]').hidden, true);
    assert.equal(f.host.children[0].dataset.error, 'false');
  });
}
