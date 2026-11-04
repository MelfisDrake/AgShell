// Fails when a Russian text in the interface code is not passed through t(), tr() or plural(), or has no English in strings.ts.
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import ts from "typescript";

const dir = new URL("../src/", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1");
const cyrillic = /[А-Яа-яЁё]/;
const parse = (file) => ts.createSourceFile(file, readFileSync(join(dir, file), "utf8"), ts.ScriptTarget.Latest, true, file.endsWith(".tsx") ? ts.ScriptKind.TSX : ts.ScriptKind.TS);

const english = new Set();
parse("strings.ts").forEachChild(function collect(node) {
  if (ts.isPropertyAssignment(node) && (ts.isStringLiteral(node.name) || ts.isIdentifier(node.name))) {
    english.add(node.name.text);
  }
  node.forEachChild(collect);
});

const problems = [];
const used = new Set();
for (const file of readdirSync(dir).filter((name) => /\.tsx?$/.test(name) && !["strings.ts", "i18n.ts"].includes(name))) {
  const source = parse(file);
  const where = (node) => `${file}:${source.getLineAndCharacterOfPosition(node.getStart()).line + 1}`;
  source.forEachChild(function visit(node) {
    if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) {
      if (cyrillic.test(node.text)) {
        const call = node.parent;
        const wrapped = ts.isCallExpression(call) && ts.isIdentifier(call.expression) && ["t", "tr", "plural"].includes(call.expression.text) && call.arguments.includes(node);
        if (!wrapped) {
          problems.push(`${where(node)} not translated: ${node.text}`);
        } else if (!english.has(node.text)) {
          problems.push(`${where(node)} no English: ${node.text}`);
        } else {
          used.add(node.text);
        }
      }
    } else if ((ts.isTemplateExpression(node) || ts.isJsxText(node)) && cyrillic.test(node.getText())) {
      problems.push(`${where(node)} not translated: ${node.getText().trim()}`);
      return;
    }
    node.forEachChild(visit);
  });
}

for (const key of english) {
  if (!used.has(key)) {
    console.warn(`strings.ts: unused: ${key}`);
  }
}
if (problems.length > 0) {
  console.error(problems.join("\n"));
  console.error(`${problems.length} untranslated texts`);
  process.exit(1);
}
