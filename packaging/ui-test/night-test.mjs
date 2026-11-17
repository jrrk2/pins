// Drives PI.N.S.'s web page (Touch-N-Stars) through a night with the Origin as its user does, in a headless browser, and checks underneath,
// through the Advanced API, that what the page shows is so. Each stage stops the test at its first failure.
//
// Usage: node night-test.mjs [stage ...]    stages: setup connect (default: all ready ones, in order)
//   PINS_HOST   the Pi (default 192.168.1.208); the page is on port 5000, the Advanced API on 1888
//   SITE        latitude,longitude in decimal degrees (default 51.47788,-0.00147)
//   Screenshots of each step go to ./shots-<time>.
import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';

const host = process.env.PINS_HOST ?? '192.168.1.208';
const page_url = `http://${host}:5000`;
const api_url = `http://${host}:1888/v2/api`;
const [latitude, longitude] = (process.env.SITE ?? '51.47788,-0.00147').split(',').map(Number);
const ORIGIN = { mount: 'Celestron Origin Mount', camera: 'Celestron Origin Camera', focuser: 'Celestron Origin Focuser', focalLength: 335, focalRatio: 2.2 };
const stages = process.argv.slice(2).length ? process.argv.slice(2) : ['setup', 'connect'];
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
  await page.getByRole('button', { name: 'Connect All', exact: true }).click();
  for (const device of ['mount', 'camera', 'focuser'])
    await until(`${device} connected`, async () => (await api(`/equipment/${device}/info`)).Connected, 120);
  await page.waitForTimeout(2000);
  await shot('connected');
}

const run = { setup, connect };
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
