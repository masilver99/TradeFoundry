// Builds the compact, locally served emoji list used by the annotation emoji search.
// Output: wwwroot/lib/unicode-emoji/emoji.json as [[emoji, name, groupIndex], ...] plus group names.
import {mkdirSync, readFileSync, writeFileSync, copyFileSync} from "node:fs";
import {dirname, join} from "node:path";
import {fileURLToPath} from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const source = join(root, "node_modules", "unicode-emoji-json");
const outputDirectory = join(root, "wwwroot", "lib", "unicode-emoji");

const byEmoji = JSON.parse(readFileSync(join(source, "data-by-emoji.json"), "utf8"));
const ordered = JSON.parse(readFileSync(join(source, "data-ordered-emoji.json"), "utf8"));
const version = JSON.parse(readFileSync(join(source, "package.json"), "utf8")).version;

const groups = [];
const emoji = [];
for (const character of ordered) {
  const entry = byEmoji[character];
  if (!entry) continue;
  let group = groups.indexOf(entry.group);
  if (group < 0) group = groups.push(entry.group) - 1;
  emoji.push([character, entry.name, group]);
}

mkdirSync(outputDirectory, {recursive: true});
writeFileSync(join(outputDirectory, "emoji.json"), JSON.stringify({version, groups, emoji}));
copyFileSync(join(source, "LICENSE"), join(outputDirectory, "LICENSE"));
console.log(`Wrote ${emoji.length} emoji in ${groups.length} groups (unicode-emoji-json ${version}).`);
