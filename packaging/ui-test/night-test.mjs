// Drives PI.N.S.'s web page (Touch-N-Stars) through a night with the Origin as its user does, in a headless browser, and checks underneath,
// through the Advanced API, that what the page shows is so. Each stage stops the test at its first failure.
//
// Usage: node night-test.mjs [stage ...]    stages, in the night's order (default: all):
//   setup connect plugins unpark align target livestack park
//   PINS_HOST   the Pi (default 192.168.1.208); the page is on port 5000, the Advanced API on 1888
//   SITE        latitude,longitude in decimal degrees (default 51.47788,-0.00147)
//   TARGET      an object to stack instead of the best placed of the list below; FRAMES  how many to stack (default 30)
//   DRY=1       goes through every stage without pressing what moves the mount or takes frames (unpark, build, slew, capture, park)
//   Screenshots of each step go to ./shots-<time>.
import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';

const host = process.env.PINS_HOST ?? '192.168.1.208';
const page_url = `http://${host}:5000`;
const api_url = `http://${host}:1888/v2/api`;
const [latitude, longitude] = (process.env.SITE ?? '51.47788,-0.00147').split(',').map(Number);
const ORIGIN = { mount: 'Celestron Origin Mount', camera: 'Celestron Origin Camera', focuser: 'Celestron Origin Focuser', focalLength: 335, focalRatio: 2.2 };
const ALL = ['setup', 'connect', 'plugins', 'unpark', 'align', 'target', 'livestack', 'park'];
const stages = process.argv.slice(2).length ? process.argv.slice(2) : ALL;
const DRY = process.env.DRY === '1';
const FRAMES = Number(process.env.FRAMES ?? 30);
// Bright, extended objects that fill the Origin's 1.3 x 0.9 degree field, as named in the page's search, with J2000 RA and Dec in degrees, best
// first (M 27 is behind the house from the Origin's place, 2026-11-16)
const TARGETS = [['NGC 869', 34.75, 57.13], ['M 31', 10.68, 41.27], ['M 45', 56.75, 24.12], ['M 57', 283.40, 33.03],
  ['M 13', 250.42, 36.46], ['M 33', 23.46, 30.66], ['M 1', 83.63, 22.01], ['M 42', 83.82, -5.39], ['M 35', 92.23, 24.33], ['M 81', 148.89, 69.07]];
const shots = `shots-${new Date().toISOString().slice(11, 19).replace(/:/g, '')}`;
mkdirSync(shots, { recursive: true });

let step_number = 0;
const log = text => console.log(`${new Date().toTimeString().slice(0, 8)} ${text}`);
class Failure extends Error {}
const fail = text => { throw new Failure(text); };

async function api(path, query = {}) {
  const url = `${api_url}${path}?${new URLSearchParams(query)}`;
  const reply = await (await fetch(url, { signal: AbortSignal.timeout(60000) })).json();
  if (!reply.Success) fail(`${path}: ${reply.Error}`);
  return reply.Response;
}
const profile = () => api('/profile/show', { active: true });
const set_profile = (path, value) => api('/profile/change-value', { settingpath: path, newValue: value });
async function until(what, condition, seconds) {
  const start = Date.now();
  while (Date.now() - start < seconds * 1000) {
    if (await condition()) { log(`  ${what}: ${((Date.now() - start) / 1000).toFixed(0)} s`); return; }
    await new Promise(r => setTimeout(r, 1000));
  }
  fail(`${what}: not within ${seconds} s`);
}

const tns = async (path, body) => {
  const reply = await fetch(`${page_url}/api${path}`, body === undefined ? { signal: AbortSignal.timeout(30000) } :
    { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal: AbortSignal.timeout(30000) });
  return reply.json();
};
const mount = () => api('/equipment/mount/info');
// Pressing what moves the mount or takes frames, unless DRY
async function act(what, button) {
  if (DRY) { log(`  (DRY: not pressing ${what})`); return false; }
  await button.click();
  return true;
}

const browser = await chromium.launch();
const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
const page = await context.newPage();
const js_errors = [];
page.on('pageerror', e => js_errors.push(e.message.split('\n')[0]));
async function shot(name) {
  await page.screenshot({ path: `${shots}/${String(++step_number).padStart(2, '0')}-${name}.png` });
}
async function open(route) {
  await page.goto(page_url + route, { waitUntil: 'load' });
  await page.waitForTimeout(2500);    // the app fetches its state after it renders; it polls, so the network never goes idle
}
// The first visit offers a tutorial over the pages: the user skips it. A browser new to the page also gets the Setup Assistant, which the
// stages after setup close with its cross (the first button in its header), as a returning user would.
async function skip_tutorial() {
  for (let pass = 0; pass < 3; pass++) {
    if (await page.getByText(/Step \d+ of \d+/).first().isVisible().catch(() => false)) {
      await page.getByText('Setup Assistant').first().locator('xpath=ancestor::*[.//button][1]').locator('button').first().click();
      await page.waitForTimeout(1000);
    }
    // then once per browser a What's New dialog, and the tutorial
    if (await page.getByText(/^What.s New/).first().isVisible().catch(() => false)) {
      await page.getByRole('button', { name: 'Close', exact: true }).last().click();
      await page.waitForTimeout(1000);
    }
    const skip = page.getByRole('button', { name: 'Skip Tutorial' });
    if (await skip.isVisible().catch(() => false)) {
      await skip.click();
      await page.waitForTimeout(1000);
    }
  }
}

const assistant = () => page.getByText(/Step \d+ of \d+/).first();
async function assistant_step() {
  return Number((await assistant().innerText()).match(/Step (\d+) of/)[1]);
}
async function assistant_to(step) {
  for (let i = 0; i < 20 && await assistant_step() < step; i++) {
    await page.getByRole('button', { name: 'Next', exact: true }).last().click();
    await page.waitForTimeout(800);
  }
  if (await assistant_step() !== step) fail(`the Setup Assistant did not reach step ${step}`);
}
// In the assistant's device steps the second list holds the devices PI.N.S. found (the first is the INDI drivers)
async function choose_device(name) {
  const list = page.locator('select', { has: page.locator(`option[value="${name}"]`) }).last();
  if (!await list.count()) fail(`${name} is not offered`);
  await list.selectOption(name);
  await page.waitForTimeout(1500);
}
// The number fields are read-only: tapping one opens the page's keypad, typed on as the user does and accepted with its tick (an unlabelled
// icon, after the cross that cancels; tapping the backdrop cancels too)
async function keypad(field, value) {
  await field.click();
  await page.waitForTimeout(500);
  const key = name => page.getByRole('button', { name, exact: true }).last();
  await key('C').click();
  for (const character of String(value)) await key(character).click();
  await page.locator('.z-numberPad').first().locator('button').nth(1).click();
  await page.waitForTimeout(800);
  const shown = await field.inputValue();
  if (Number(shown) !== Number(value)) fail(`the keypad left ${shown} in the field, not ${value}`);
}

// setup: the Setup Assistant as a new user goes through it, then checks the profile holds what was entered, and that the page shows it after
// a reload. The settings it should save are cleared first, so the check shows the assistant saved them; if it did not, the profile is put
// back as it was, so a failed test does not leave the Pi unusable for the night.
async function setup() {
  const before = await profile();
  const restore = async () => {
    await set_profile('AstrometrySettings-Latitude', before.AstrometrySettings.Latitude);
    await set_profile('AstrometrySettings-Longitude', before.AstrometrySettings.Longitude);
    await set_profile('TelescopeSettings-FocalLength', before.TelescopeSettings.FocalLength);
    for (const [device, settings] of [['camera', 'CameraSettings'], ['mount', 'TelescopeSettings'], ['focuser', 'FocuserSettings']])
      await set_profile(`${settings}-Id`, before[settings].Id);
  };
  log('Clearing the settings the Setup Assistant should save');
  for (const [path, value] of [['AstrometrySettings-Latitude', 0], ['AstrometrySettings-Longitude', 0], ['TelescopeSettings-FocalLength', 100],
    ['CameraSettings-Id', 'No_Device'], ['TelescopeSettings-Id', 'No_Device'], ['FocuserSettings-Id', 'No_Device']])
    await set_profile(path, value);
  try {
    log('Opening the page: the Setup Assistant');
    await open('/');
    if (!await assistant().isVisible().catch(() => false)) fail('a new browser is not offered the Setup Assistant');
    await shot('assistant');

    log('Mount: Celestron Origin Mount');
    await assistant_to(7);
    await choose_device(ORIGIN.mount);
    await shot('assistant-mount');

    log(`Location: ${latitude}, ${longitude}, then Save location`);
    await assistant_to(9);
    const fields = page.locator('input[type=text]');
    await fields.nth(0).fill(String(latitude));
    await fields.nth(1).fill(String(longitude));
    await page.getByRole('button', { name: 'Save location' }).click();
    await page.waitForTimeout(1500);
    await shot('assistant-location');

    log(`Telescope: focal length ${ORIGIN.focalLength} mm`);
    await assistant_to(10);
    await keypad(page.locator('#wizard-focal-length'), ORIGIN.focalLength);
    await shot('assistant-telescope');

    log('Camera: Celestron Origin Camera');
    await assistant_to(12);
    await choose_device(ORIGIN.camera);
    await shot('assistant-camera');

    log('Focuser: Celestron Origin Focuser');
    await assistant_to(13);
    await choose_device(ORIGIN.focuser);
    await shot('assistant-focuser');

    log('Finish');
    await assistant_to(16);
    await page.getByRole('button', { name: 'Finish', exact: true }).click();
    await page.waitForTimeout(1500);
    await shot('assistant-finished');

    log('Checking the profile holds what was entered');
    const after = await profile();
    const wrong = [];
    const expect = (what, got, want) => { if (typeof want === 'number' ? Math.abs(got - want) > 1e-4 : got !== want) wrong.push(`${what} is ${got}, not ${want}`); };
    expect('latitude', after.AstrometrySettings.Latitude, latitude);
    expect('longitude', after.AstrometrySettings.Longitude, longitude);
    expect('focal length', after.TelescopeSettings.FocalLength, ORIGIN.focalLength);
    expect('mount', after.TelescopeSettings.Id, 'Origin_Mount');
    expect('camera', after.CameraSettings.Id, 'Origin_Camera');
    expect('focuser', after.FocuserSettings.Id, 'Origin_Focuser');
    if (wrong.length) fail(`the Setup Assistant did not save: ${wrong.join('; ')}`);

    log('Reloading: the assistant stays closed and the equipment page shows the Origin');
    await open('/equipment');
    await skip_tutorial();
    if (await assistant().isVisible().catch(() => false)) fail('the Setup Assistant opens again after Finish');
    for (const name of [ORIGIN.mount, ORIGIN.camera, ORIGIN.focuser]) {
      const chosen = await page.locator('select', { has: page.locator(`option[value="${name}"]`) }).first().inputValue();
      if (chosen !== name) fail(`the equipment page shows ${chosen}, not ${name}`);
    }
    await shot('equipment-after-reload');
  } catch (e) {
    await shot('failed').catch(() => {});
    log('Putting the profile back as it was');
    await restore();
    throw e;
  }
}

// connect: Connect All on the equipment page, then the mount, camera and focuser are connected, as the page and PI.N.S. both say
async function connect() {
  log('Equipment page: Connect All');
  await open('/equipment');
  await skip_tutorial();
  await shot('equipment');
  const connect_all = page.getByRole('button', { name: 'Connect All', exact: true });
  if (await connect_all.isDisabled()) {
    log('  Connect All is greyed out: connected already?');
  } else await connect_all.click();
  for (const device of ['mount', 'camera', 'focuser'])
    await until(`${device} connected`, async () => (await api(`/equipment/${device}/info`)).Connected, 120);
  await page.waitForTimeout(2000);
  await shot('connected');
}

async function nav(route) {
  await page.locator(`a[href="${route}"]`).first().click();
  await page.waitForTimeout(2500);
}
// A plugin's page, by its name in the navigation bar (its address depends on the plugins installed)
async function plugin_page(name) {
  const link = page.locator('a[href^="/plugin"]').filter({ hasText: name }).first();
  if (!await link.count()) fail(`no ${name} in the navigation bar: is it switched on in Settings, Plugin Management?`);
  await link.click();
  await page.waitForTimeout(3000);
}

// plugins: Settings, Plugin Management, the livestack and 10micron Model Builder pages switched on; they are a browser's own setting
async function plugins() {
  await open('/equipment');
  await skip_tutorial();
  await nav('/settings');
  await page.getByRole('button', { name: 'Plugin Management' }).first().click();
  await page.waitForTimeout(2000);
  // the switch's aria-pressed does not follow it, so on is what the user sees: the plugin's page in the navigation bar
  const listed = name => page.locator('a[href^="/plugin"]').filter({ hasText: name }).count();
  for (const name of ['livestack', '10micron Model Builder']) {
    if (await listed(name)) continue;
    log(`Switching on ${name}`);
    await page.getByText(name, { exact: true }).first().locator('xpath=ancestor::*[.//button[normalize-space()="Toggle"]][1]')
      .getByRole('button', { name: 'Toggle' }).first().click();
    await page.waitForTimeout(1500);
  }
  await shot('plugins');
  for (const name of ['livestack', '10micron Model Builder'])
    if (!await page.locator('a[href^="/plugin"]').filter({ hasText: name }).count()) fail(`${name} is switched on but not in the navigation bar`);
}

// unpark: the Mount page's Unpark: the Origin initialises (with the profile's site) and plate solves, then the page and PI.N.S. say unparked
async function unpark() {
  await open('/equipment');
  await skip_tutorial();
  await nav('/mount');
  await shot('mount-parked');
  if (!(await mount()).AtPark) { log('  the mount is unparked already'); return; }
  if (!await act('Unpark', page.getByRole('button', { name: 'Unpark', exact: true }).first())) return;
  log('Unparking: initialisation and a plate solve, a few minutes');
  await until('unparked', async () => !(await mount()).AtPark, 900);
  await page.waitForTimeout(3000);
  await shot('unparked');
  if (await page.getByText('Parked', { exact: true }).first().isVisible().catch(() => false)) fail('PI.N.S. says unparked, the page still says Parked');
}

// align: the 10micron Model Builder page: 12 points of the golden spiral above 30 degrees (the trees take the lowest), Build Model, then the
// page's Alignment Model and PI.N.S. both hold a model of at least 8 stars
async function align() {
  await open('/equipment');
  await skip_tutorial();
  await plugin_page('10micron Model Builder');
  await page.getByRole('button', { name: 'Model Builder', exact: true }).first().click();
  await page.waitForTimeout(1000);
  if (Number(await page.locator('#tm-star-count').inputValue()) !== 12) await keypad(page.locator('#tm-star-count'), 12);
  if (Number(await page.locator('#tm-min-alt').inputValue()) !== 30) {
    // in a folded section
    for (const section of ['Point Filters', 'Build Options']) {
      if (await page.locator('#tm-min-alt').isVisible()) break;
      await page.getByText(section, { exact: true }).first().click();
      await page.waitForTimeout(800);
    }
    await keypad(page.locator('#tm-min-alt'), 30);
  }
  log('Generate Golden Spiral: 12 points');
  if (await page.getByRole('button', { name: 'Clear Points' }).first().isEnabled()) {
    await page.getByRole('button', { name: 'Clear Points' }).first().click();
    await page.waitForTimeout(1500);
  }
  await page.getByRole('button', { name: 'Generate Golden Spiral' }).first().click();
  await until('12 points generated', async () => (await tns('/tenmicron/builder-status')).ModelPoints?.length === 12, 30);
  await shot('model-points');
  if (!await page.getByText(/Points: 12 usable/).first().isVisible().catch(() => false)) fail('the page does not show the 12 points');
  if (!await act('Build Model', page.getByRole('button', { name: 'Build Model' }).first())) return;
  log('Building the model: a slew and a plate solve a point, about 15 minutes');
  await until('build started', async () => (await tns('/tenmicron/builder-status')).BuildInProgress, 60);
  let last = '';
  await until('build done', async () => {
    const status = await tns('/tenmicron/builder-status');
    const states = (status.ModelPoints ?? []).map(p => p.StateLabel).join(' ');
    if (states !== last) { log(`  points: ${states}`); last = states; }
    return !status.BuildInProgress;
  }, 3600);
  await page.getByRole('button', { name: 'Alignment Model', exact: true }).first().click();
  await page.waitForTimeout(2500);
  await shot('alignment-model');
  const model = await tns('/tenmicron/alignment-model');
  if (!model.ModelLoaded) fail('no model after the build');
  log(`  model: ${model.AlignmentStarCount} stars, RMS ${model.RMSError}", tilt ${model.PolarAlignErrorDegrees?.toFixed(2)} degrees`);
  if (model.AlignmentStarCount < 8) fail(`only ${model.AlignmentStarCount} stars in the model`);
  if (!await page.getByText(String(model.AlignmentStarCount)).first().isVisible().catch(() => false)) fail('the page does not show the model');
}

// target: the best placed of TARGETS (40 to 75 degrees up, for an alt-az mount's field rotation and the trees), or TARGET, searched for on
// the Mount page's Slew tab and slewed to; then PI.N.S. says the mount is within 10 arcminutes of it
let target;
async function target_stage() {
  const info = await mount();
  const lst = (info.SiderealTime ?? 0) * 15, lat = latitude * Math.PI / 180;
  const altitude = (ra, dec) => {
    const h = (lst - ra) * Math.PI / 180, d = dec * Math.PI / 180;
    return Math.asin(Math.sin(lat) * Math.sin(d) + Math.cos(lat) * Math.cos(d) * Math.cos(h)) * 180 / Math.PI;
  };
  if (process.env.TARGET) target = [process.env.TARGET, NaN, NaN];
  else {
    const placed = TARGETS.map(t => [...t, altitude(t[1], t[2])]).filter(t => t[3] >= 40 && t[3] <= 75);
    if (!placed.length) fail(`none of ${TARGETS.map(t => t[0]).join(', ')} is 40 to 75 degrees up`);
    target = placed[0];
    log(`Target: ${target[0]}, ${target[3].toFixed(0)} degrees up`);
  }
  await open('/equipment');
  await skip_tutorial();
  await nav('/mount');
  await page.getByRole('button', { name: 'Slew', exact: true }).first().click();
  await page.waitForTimeout(1500);
  const search = page.getByPlaceholder('Enter a search term...');
  await search.click();
  await search.pressSequentially(target[0], { delay: 100 });
  await page.waitForTimeout(2500);
  const hit = page.locator('li').filter({ hasText: new RegExp(`^\\s*${target[0].replace(' ', '\\s*')}\\b`) }).first();
  if (!await hit.count()) fail(`the search offers nothing for ${target[0]}`);
  await hit.click();
  await page.waitForTimeout(2000);
  await shot('target-picked');
  const slew = page.getByRole('button', { name: 'Slew', exact: true }).last();
  await slew.scrollIntoViewIfNeeded();
  if (!await act(`Slew to ${target[0]}`, slew)) return;
  await page.waitForTimeout(3000);
  await until('slew done', async () => !(await mount()).Slewing, 600);
  await page.waitForTimeout(5000);    // the Origin settles after a slew: a frame straight after one trails
  const at = await mount();
  if (!Number.isNaN(target[1])) {
    const ra_now = at.RightAscension * 15, dec_now = at.Declination;
    const miss = Math.hypot(((ra_now - target[1] + 540) % 360 - 180) * Math.cos(dec_now * Math.PI / 180), dec_now - target[2]) * 60;
    log(`  at RA ${ra_now.toFixed(2)} Dec ${dec_now.toFixed(2)}, ${miss.toFixed(1)} arcmin from ${target[0]} (J2000; the mount reports JNow)`);
    if (miss > 30) fail(`the mount is ${miss.toFixed(0)} arcmin from ${target[0]}`);
  }
  await shot('at-target');
}

// livestack: the livestack page started, then the Camera page takes FRAMES saved 5 s frames in a loop; LiveStack stacks them, and each is plate
// solved: the drift and rotation over the run show how well the Origin tracks for stacking
async function livestack() {
  await open('/equipment');
  await skip_tutorial();
  // the Camera page first: 5 s frames, and in its settings' Save & Solve, Save snapshots on (LiveStack stacks saved frames), Use Platesolve on,
  // and the target's name
  await nav('/camera');
  const exposure = page.locator('#exposure').first();
  await exposure.fill('5');
  await exposure.press('Enter');
  await page.locator('#exposure').first().locator('xpath=ancestor::div[3]').locator('button').last().click();
  await page.waitForTimeout(1200);
  await page.getByRole('button', { name: 'Save & Solve', exact: true }).first().click();
  await page.waitForTimeout(1200);
  const toggle = label => page.getByText(label, { exact: true }).first().locator('xpath=ancestor::*[.//button][1]').getByRole('button', { name: 'Toggle' }).first();
  for (const label of ['Use Platesolve', 'Save snapshots']) {
    if (await toggle(label).getAttribute('aria-pressed') !== 'true') {
      log(`Camera settings: ${label} on`);
      await toggle(label).click();
      await page.waitForTimeout(1000);
    }
    if (await toggle(label).getAttribute('aria-pressed') !== 'true') fail(`${label} does not stay on`);
  }
  const name = target?.[0] ?? process.env.TARGET ?? 'test';
  await page.locator('#targetName').fill(name.replace(' ', ''));
  await page.locator('#targetName').press('Tab');
  await shot('camera-save-solve');
  await page.getByRole('button', { name: 'Close', exact: true }).last().click();
  await page.waitForTimeout(1000);

  // then the livestack page: Reset stack (the bin), and the green play button before it starts LiveStack
  await plugin_page('livestack');
  const reset = page.getByRole('button', { name: 'Reset stack' }).first();
  await reset.click();
  await page.waitForTimeout(1000);
  const play = reset.locator('xpath=preceding-sibling::button[1]');
  await shot('livestack-page');
  if (!DRY) {
    await play.click();
    await until('LiveStack started', async () => /run|start|active|true/i.test(JSON.stringify(await api('/livestack/status'))), 30);
    log(`  LiveStack: ${JSON.stringify(await api('/livestack/status'))}`);
  } else log('  (DRY: not pressing LiveStack start)');
  const history_before = (await api('/image-history', { count: true })) ?? 0;
  await nav('/camera');
  const capture = page.locator('button[data-haptic]').first();
  if (!await act(`capture ${FRAMES} frames`, capture)) return;
  log(`Taking ${FRAMES} frames of 5 s`);
  const first = Date.now();
  for (let n = 1; n < FRAMES; n++) {
    const count = async () => (await api('/image-history', { count: true })) - history_before;
    await until(`frame ${n}`, async () => await count() >= n, 120);
    await capture.click();
  }
  await until(`frame ${FRAMES}`, async () => (await api('/image-history', { count: true })) - history_before >= FRAMES, 120);
  const minutes = (Date.now() - first) / 60000;
  await plugin_page('livestack');
  await shot('livestack-stacked');
  const status = await api('/livestack/status');
  log(`  LiveStack: ${JSON.stringify(status)}`);
  log('Plate solving the first and last frames');
  const solve = async index => {
    const r = await api(`/image/${index}/solve`);
    if (!r?.Success) fail(`frame ${index} does not plate solve`);
    return r;
  };
  const a = await solve(history_before), b = await solve(history_before + FRAMES - 1);
  const ra = c => (c.Coordinates.RADegrees ?? c.Coordinates.RA * 15), dec = c => c.Coordinates.Dec;
  const drift = Math.hypot(((ra(b) - ra(a) + 540) % 360 - 180) * Math.cos(dec(a) * Math.PI / 180), dec(b) - dec(a)) * 3600;
  const rotation = ((b.PositionAngle - a.PositionAngle + 540) % 360) - 180;
  log(`  tracking over ${minutes.toFixed(1)} min: drift ${drift.toFixed(0)}" (${(drift / minutes).toFixed(1)}"/min), rotation ${rotation.toFixed(2)} degrees (${(rotation / minutes).toFixed(2)}/min)`);
  if (drift / minutes > 30) fail(`the field drifts ${(drift / minutes).toFixed(0)}"/min`);
}

// park: the Mount page's Park, then the page and PI.N.S. say parked
async function park() {
  await open('/equipment');
  await skip_tutorial();
  await nav('/mount');
  if ((await mount()).AtPark) { log('  parked already'); return; }
  if (!await act('Park', page.getByRole('button', { name: 'Park', exact: true }).first())) return;
  await until('parked', async () => (await mount()).AtPark, 600);
  await page.waitForTimeout(3000);
  await shot('parked');
}

const run = { setup, connect, plugins, unpark, align, target: target_stage, livestack, park };
let failed = false;
for (const stage of stages) {
  if (!run[stage]) { console.error(`No stage ${stage}: ${Object.keys(run).join(' ')}`); process.exit(2); }
  log(`== ${stage}`);
  try {
    await run[stage]();
    log(`PASS ${stage}`);
  } catch (e) {
    log(`FAIL ${stage}: ${e instanceof Failure ? e.message : e.stack}`);
    failed = true;
    break;
  }
}
if (js_errors.length) log(`JavaScript errors in the page: ${[...new Set(js_errors)].join(' | ')}`);
log(`Screenshots in ${process.cwd()}/${shots}`);
await browser.close();
process.exit(failed ? 1 : 0);
