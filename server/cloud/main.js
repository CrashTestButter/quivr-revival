/* global Parse */
// Cloud functions called by the QuiVr client (Assembly-CSharp: Armory, ItemGenerator, SteamSetup).
//
// Inventories are stored on the user (or ArcadeUser) as one string:
//   item$item$item
// where each item is ArmorOption.ToString():
//   Name%ItemType%ObjectID%r,g,b,a:r,g,b,a%Rarity%Ancient|EffectID^Value

const MASTER = { useMasterKey: true };
const SEP = '$';

function splitInventory(s) {
  return typeof s === 'string' && s.length > 1 ? s.split(SEP).filter(i => i.length > 5) : [];
}

// Identity of an item ignoring its rolled stats: "Name%ItemType".
function itemKey(item) {
  return item.split('|')[0].split('%').slice(0, 2).join('%');
}

function isValidItem(item) {
  const [head, effect] = item.split('|');
  if (effect === undefined || !/^\d+\^/.test(effect)) return false;
  const parts = head.split('%');
  return parts.length >= 4 && parts[0].length > 0 && parts[0] !== 'Undefined';
}

// Loads the object holding the inventory and checks the caller owns it.
async function loadOwner(request, userid, arcade) {
  if (!request.user) throw new Parse.Error(Parse.Error.INVALID_SESSION_TOKEN, 'Not logged in');
  if (arcade) {
    const obj = await new Parse.Query('ArcadeUser').get(userid, MASTER);
    const owner = obj.get('Owner');
    if (!owner || owner.id !== request.user.id) throw new Parse.Error(Parse.Error.OPERATION_FORBIDDEN, 'Not your arcade user');
    return obj;
  }
  if (userid !== request.user.id) throw new Parse.Error(Parse.Error.OPERATION_FORBIDDEN, 'Not your user');
  return new Parse.Query(Parse.User).get(userid, MASTER);
}

async function saveInventory(obj, inventory) {
  obj.set('Inventory', inventory.join(SEP));
  await obj.save(null, MASTER);
}

// Armory.AddItemSync: appends one item, or a "$"-joined list (the starter set).
Parse.Cloud.define('AddItem', async request => {
  const { userid, arcade, item } = request.params;
  const owner = await loadOwner(request, userid, !!arcade);
  const inventory = splitInventory(owner.get('Inventory'));
  let added = 0;
  for (const it of splitInventory(item)) {
    if (!inventory.includes(it)) {
      inventory.push(it);
      added++;
    }
  }
  await saveInventory(owner, inventory);
  return `added ${added}`;
});

// Armory.ReplaceItemSync: reforge (old -> new) or disenchant (new is empty).
Parse.Cloud.define('ReplaceItemV3', async request => {
  const { userid, arcade, olditem, newitem } = request.params;
  const owner = await loadOwner(request, userid, !!arcade);
  const inventory = splitInventory(owner.get('Inventory'));
  let idx = inventory.indexOf(olditem);
  if (idx < 0 && olditem) idx = inventory.findIndex(i => itemKey(i) === itemKey(olditem));
  if (idx >= 0) {
    if (newitem) inventory[idx] = newitem;
    else inventory.splice(idx, 1);
  } else if (newitem) {
    inventory.push(newitem);
  }
  await saveInventory(owner, inventory);
  return idx >= 0 ? 'replaced' : 'appended';
});

// ItemGenerator.GetRandomItem: pick a catalog item of the requested rarity.
// Prefers items the player doesn't own yet, and the slot they have the fewest items in.
// The client adds the returned item itself via AddItem.
Parse.Cloud.define('GetItemV2', async request => {
  const rarity = Number(request.params.rarity);
  const owned = new Set(request.params.owned || []);
  const wantedType = Number(request.params.itemtype);
  const all = await new Parse.Query('Items').equalTo('Rarity', rarity).limit(1000).find(MASTER);
  if (!all.length) return '';
  const notOwned = all.filter(o => !owned.has(o.get('Name')));
  const pool = notOwned.length ? notOwned : all;
  const wanted = pool.filter(o => o.get('ItemType') === wantedType);
  const choices = wanted.length ? wanted : pool;
  const pick = choices[Math.floor(Math.random() * choices.length)];
  // Same shape the original server returned (see ItemGenerator.cs in the PSVR source).
  return JSON.stringify({
    Rarity: pick.get('Rarity'),
    Name: pick.get('Name'),
    EffectID: pick.get('EffectID'),
    ItemType: pick.get('ItemType'),
    Meshes: String(pick.get('Meshes')),
    Colors: pick.get('Colors') || '',
  });
});

// SteamSetup.QueryVersion calls this on every start; drop broken and duplicate entries.
Parse.Cloud.define('FixInventories', async request => {
  if (!request.user) return '0';
  const user = await new Parse.Query(Parse.User).get(request.user.id, MASTER);
  const before = splitInventory(user.get('Inventory'));
  const after = [...new Set(before.filter(isValidItem))];
  if (after.length !== before.length) await saveInventory(user, after);
  return String(before.length - after.length);
});

// GetRatity() creates "NxtLgdr" with ParseObject.Add (an array op) but later reads it
// with Get<int>, which throws for an array. Store it as a plain number instead.
Parse.Cloud.beforeSave(Parse.User, request => {
  const v = request.object.get('NxtLgdr');
  if (Array.isArray(v)) {
    const n = Number(v[v.length - 1]);
    request.object.set('NxtLgdr', Number.isFinite(n) ? n : 50);
  }
});
