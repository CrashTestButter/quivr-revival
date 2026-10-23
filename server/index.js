const crypto = require('crypto');
const path = require('path');
const express = require('express');
const { ParseServer } = require('parse-server');
const seed = require('./seed');

const PORT = 1337;
const MOUNT = '/parse';

// The game ships with these values compiled in (ParseInitBehavior in
// Assembly-CSharp-firstpass.dll), so the server has to use them as-is.
const APP_ID = 'QuiVr';
const DOTNET_KEY = 'H22S9I95anJC6ipm0UgZeabFp819Dr4q';

async function main() {
  const masterKey = process.env.MASTER_KEY || crypto.randomBytes(24).toString('hex');

  const server = new ParseServer({
    appId: APP_ID,
    dotNetKey: DOTNET_KEY,
    masterKey,
    // Master key is only used by this process (seeding) unless MASTER_KEY_IPS widens it.
    masterKeyIps: (process.env.MASTER_KEY_IPS || '127.0.0.1,::1').split(','),
    databaseURI: process.env.DATABASE_URI || 'mongodb://localhost:27017/quivr',
    cloud: path.join(__dirname, 'cloud', 'main.js'),
    serverURL: `http://127.0.0.1:${PORT}${MOUNT}`,
    publicServerURL: process.env.PUBLIC_SERVER_URL,
    // All classes the game uses are created by seed.js.
    allowClientClassCreation: false,
    enforcePrivateUsers: true,
    encodeParseObjectInCloudFunction: true,
    enableInsecureAuthAdapters: false,
    databaseOptions: { allowPublicExplain: false },
    pages: { enableRouter: true },
    logLevel: process.env.LOG_LEVEL || 'info',
  });

  await server.start();

  const app = express();
  app.use(MOUNT, server.app);
  app.get('/health', (req, res) => res.json({ status: 'ok' }));
  app.listen(PORT, () => console.log(`QuiVr server listening on :${PORT}${MOUNT}`));

  await seed.run();
}

main().catch(err => {
  console.error(err);
  process.exit(1);
});
