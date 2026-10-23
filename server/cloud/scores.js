/* global Parse */
// Leaderboards (ScoreDatabase in the client). One row per player and board:
//   Score, UserID (username, or "username_ArcadeName"), Username (display name), BoardID, VersionEarned
// The singleplayer (FFKDiff) and multiplayer (CanyonMPDiff) boards also decide who
// gets the temporary "Bow of Trials" / "Victorious Longbow".

const SCORE_CLASSES = ['FFKDiff', 'CanyonMPDiff', 'ArcheryRange01', 'OwlInvaders'];

function register() {
  for (const board of SCORE_CLASSES) {
    Parse.Cloud.beforeSave(board, request => {
      if (request.master) return;
      const user = request.user;
      if (!user) throw new Parse.Error(Parse.Error.OPERATION_FORBIDDEN, 'Not logged in');
      const name = user.getUsername();
      const id = request.object.get('UserID');
      if (id !== name && !(typeof id === 'string' && id.startsWith(`${name}_`))) {
        throw new Parse.Error(Parse.Error.OPERATION_FORBIDDEN, 'Not your score');
      }
      if (!request.original) {
        const acl = new Parse.ACL(user);
        acl.setPublicReadAccess(true);
        request.object.setACL(acl);
      }
    });
  }
}

module.exports = { SCORE_CLASSES, register };
