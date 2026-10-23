#!/usr/bin/env bash
# Replays the requests QuiVr's Parse SDK makes (every call is a POST with "_method" in the body)
# against a running server and checks the answers.
#   tests/smoke.sh [base url]      default http://127.0.0.1:4040/parse
set -u
B=${1:-http://127.0.0.1:4040/parse}
APP=(-H 'X-Parse-Application-Id: QuiVr' -H 'X-Parse-Windows-Key: H22S9I95anJC6ipm0UgZeabFp819Dr4q' -H 'Content-Type: application/json')
U=7656119$(printf '%010d' $((RANDOM * RANDOM)))
fail=0

# call METHOD PATH [JSON] [TOKEN]  -> sends like the game does
call() {
  local method=$1 path=$2 data=${3:-} token=${4:-} body
  if [ -z "$data" ]; then body="{\"_method\":\"$method\",\"_noBody\":true}"; else body=$(jq -c --arg m "$method" '. + {_method:$m}' <<<"$data"); fi
  if [ "$method" = POST ] && [ -n "$data" ]; then body=$data; fi
  curl -s "${APP[@]}" ${token:+-H "X-Parse-Session-Token: $token"} -X POST "$B/$path" -d "$body"
}
check() { # name, actual, jq filter that must be true
  if jq -e "$3" >/dev/null 2>&1 <<<"$2"; then echo "ok   $1"; else echo "FAIL $1: $2"; fail=1; fi
}

check "online check" "$(curl -s "$B/")" '.error == "unauthorized"'
check "config" "$(call GET config)" '.params.RespawnTime == 3'
R=$(call GET "login?username=$U&password=$U"); check "first login creates account" "$R" '.sessionToken and .name == "Archer"'
T=$(jq -r .sessionToken <<<"$R"); ID=$(jq -r .objectId <<<"$R")
check "wrong password" "$(call GET "login?username=$U&password=nope")" '.code == 101'
check "second login" "$(call GET "login?username=$U&password=$U")" '.objectId == "'$ID'"'
check "fetch self" "$(call GET classes/_User/$ID "" $T)" '.username == "'$U'"'
check "users/me" "$(call GET users/me "" $T)" '.objectId == "'$ID'"'
check "rename + increment + NxtLgdr fix" \
  "$(call PUT classes/_User/$ID '{"name":"Tester","Resource":{"__op":"Increment","amount":615},"NxtLgdr":{"__op":"Add","objects":[42]},"FullVersion":true}' $T)" \
  '.Resource == 615 and .NxtLgdr == 42 and .updatedAt'
check "decrement" "$(call PUT classes/_User/$ID '{"Resource":{"__op":"Increment","amount":-15}}' $T)" '.Resource == 600'
check "FixInventories" "$(call POST functions/FixInventories '{}' $T)" '.result == "0"'
check "AddItem starter set" "$(call POST functions/AddItem '{"userid":"'$ID'","arcade":false,"item":"Starwood Arrow%0%0% %0%0|0^0$Forceful Mitts%3%1% %0%0|2^5.5"}' $T)" '.result == "added 2"'
check "GetItemV2" "$(call POST functions/GetItemV2 '{"rarity":4,"owned":["Voidwraps"],"itemtype":3}' $T)" '(.result | fromjson | .Rarity == 4 and .ItemType == 3 and .Name != "Voidwraps" and (.Meshes|type) == "string")'
check "ReplaceItemV3" "$(call POST functions/ReplaceItemV3 '{"userid":"'$ID'","arcade":false,"olditem":"Forceful Mitts%3%1% %0%0|2^5.5","newitem":"Forceful Mitts%3%1% %0%1|2^7.25"}' $T)" '.result == "replaced"'
check "inventory saved" "$(call GET classes/_User/$ID "" $T)" '.Inventory == "Starwood Arrow%0%0% %0%0|0^0$Forceful Mitts%3%1% %0%1|2^7.25" and .Resource == 600 and .NxtLgdr == 42 and .name == "Tester"'
check "AddItem for another user" "$(call POST functions/AddItem '{"userid":"abcdefghij","arcade":false,"item":"x%0%0% %0%0|0^0"}' $T)" '.code == 119'
check "unknown function" "$(call POST functions/Nope '{}' $T)" '.code == 141'
check "items catalog" "$(call GET 'classes/Items?limit=1000')" '(.results | length) >= 40 and (.results[0] | has("Name") and has("Rarity"))'
check "items read-only" "$(call POST classes/Items '{"Name":"Hax"}' $T)" '.code == 119'
check "VersionInfo first (none)" "$(call GET 'classes/VersionInfo?where=%7B%22Version%22%3A%22test-'$U'%22%7D&limit=1' "" $T)" '.results == []'
V=$(call POST classes/VersionInfo '{"Version":"test-'$U'","Sessions":1,"TimePlayed":10}' $T); check "VersionInfo create" "$V" '.objectId and .createdAt'
check "VersionInfo increment" "$(call PUT classes/VersionInfo/$(jq -r .objectId <<<"$V") '{"Sessions":{"__op":"Increment","amount":1}}' $T)" '.Sessions == 2'
check "score create" "$(call POST classes/FFKDiff '{"Score":1247,"UserID":"'$U'","Username":"Tester","VersionEarned":"1.1.44","BoardID":"Main"}' $T)" '.objectId'
check "score for someone else" "$(call POST classes/FFKDiff '{"Score":9,"UserID":"76561198000000000","Username":"x","BoardID":"Main"}' $T)" '.code == 119'
W=$(jq -rn --arg u "$U" '{UserID:$u, BoardID:"Main", updatedAt:{"$gt":{"__type":"Date","iso":"2000-01-01T00:00:00.000Z"}}} | @uri')
check "own score lookup" "$(call GET "classes/FFKDiff?where=$W&limit=1" "" $T)" '.results[0].Score == 1247'
W=$(jq -rn '{BoardID:"Main"} | @uri')
check "top list" "$(call GET "classes/FFKDiff?where=$W&order=-Score,-Username&limit=10")" '(.results | length) >= 1 and (.results | map(.Score) | . == (sort | reverse))'
A=$(call POST classes/ArcadeUser '{"Name":"Arc'$U'","Pin":"aaaa","Owner":{"__type":"Pointer","className":"_User","objectId":"'$ID'"}}' $T)
check "arcade user" "$A" '.objectId'
check "arcade AddItem" "$(call POST functions/AddItem '{"userid":"'$(jq -r .objectId <<<"$A")'","arcade":true,"item":"Plain Mask%2%0% %0%0|0^0"}' $T)" '.result == "added 1"'
check "unknown class" "$(call GET classes/Nope "" $T)" '.code == 119'
check "user query denied" "$(call GET classes/_User "" $T)" '.code == 119'
check "bad session" "$(call GET config "" r:bogus)" '.code == 209'
check "logout" "$(call POST logout '{}' $T)" '. == {}'
check "token gone after logout" "$(call GET users/me "" $T)" '.code == 209'
exit $fail
