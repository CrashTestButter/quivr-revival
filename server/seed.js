/* global Parse */
const items = require('./data/items.json');

const MASTER = { useMasterKey: true };

// Values the game reads from ParseConfig. Existing values are never overwritten,
// so an operator can change them in the dashboard / REST API.
const CONFIG_DEFAULTS = {
  ServerMessage: '',
  BetaMessage: '',
  WeeklySeed: '0',
  RespawnTime: 3,
  InvincibleTime: 5,
  DiffMult: 1,
};

const PUBLIC_READ_ONLY = {
  find: { '*': true }, get: { '*': true }, count: { '*': true },
  create: {}, update: {}, delete: {}, addField: {},
};
const PUBLIC_WRITE = {
  find: { '*': true }, get: { '*': true }, count: { '*': true },
  create: { '*': true }, update: { '*': true }, delete: {}, addField: { '*': true },
};

async function ensureClass(name, fields, clp) {
  const schema = new Parse.Schema(name);
  let existing = null;
  try {
    existing = await schema.get();
  } catch (e) {
    if (e.code !== Parse.Error.INVALID_CLASS_NAME) throw e;
  }
  if (!existing) {
    for (const [field, type] of Object.entries(fields)) schema.addField(field, type);
    schema.setCLP(clp);
    await schema.save();
    console.log(`seed: created class ${name}`);
  }
}

async function seedItems() {
  const existing = await new Parse.Query('Items').limit(1000).find(MASTER);
  const byName = new Map(existing.map(o => [o.get('Name'), o]));
  const changed = [];
  for (const item of items) {
    const obj = byName.get(item.Name) || new Parse.Object('Items');
    let dirty = !obj.id;
    for (const [k, v] of Object.entries(item)) {
      if (obj.get(k) !== v) {
        obj.set(k, v);
        dirty = true;
      }
    }
    if (dirty) changed.push(obj);
  }
  if (changed.length) {
    await Parse.Object.saveAll(changed, MASTER);
    console.log(`seed: wrote ${changed.length} item(s)`);
  }
}

async function seedConfig() {
  const config = await Parse.Config.get(MASTER);
  const missing = {};
  for (const [k, v] of Object.entries(CONFIG_DEFAULTS)) {
    if (config.get(k) === undefined) missing[k] = v;
  }
  if (Object.keys(missing).length) {
    await Parse.Config.save(missing);
    console.log(`seed: set config ${Object.keys(missing).join(', ')}`);
  }
}

async function run() {
  await ensureClass('Items', {
    Name: 'String', ItemType: 'Number', Rarity: 'Number', Meshes: 'Number', EffectID: 'Number', Colors: 'String',
  }, PUBLIC_READ_ONLY);
  // Both are created and updated directly by the game client.
  await ensureClass('VersionInfo', { Version: 'String', Sessions: 'Number', TimePlayed: 'Number' }, PUBLIC_WRITE);
  await ensureClass('ArcadeUser', { Name: 'String', Pin: 'String' }, PUBLIC_WRITE);
  // Analytics counters (SingleUseAnalytics); the game only fetches and increments existing rows.
  await ensureClass('PlayerMovement', {}, PUBLIC_WRITE);
  await seedItems();
  await seedConfig();
  console.log(`seed: done (${items.length} items in catalog)`);
}

module.exports = { run };
