import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const repo = 'https://github.com/rkingon/Levee';
export const base = '/Levee/';
export const root = fileURLToPath(new URL('../../../', import.meta.url));
export const read = (relative) => fs.readFileSync(path.join(root, relative), 'utf8');

export function rules() {
	const sections = [];
	let current = { lines: [] };
	let fenced = false;
	for (const line of read('docs/rules.md').split(/\r?\n/)) {
		if (/^\s*```/.test(line)) fenced = !fenced;
		if (!fenced && line.startsWith('## ')) {
			const match = /^## (LEVEE\d{3}): (.+)$/.exec(line);
			if (!match) throw new Error(`Unexpected rule heading: ${line}`);
			sections.push(current);
			current = { id: match[1], slug: match[1].toLowerCase(), title: match[2], lines: [] };
		} else {
			current.lines.push(line);
		}
	}
	sections.push(current);
	const intro = sections.shift().lines.slice(1).join('\n').trim();
	return { intro, list: sections.map(({ lines, ...rule }) => ({ ...rule, body: lines.join('\n').trim() })) };
}

// The landing example should change whenever the compiler's instructions do.
export function saveChangesDiagnostic(source) {
	const descriptor = /DiagnosticDescriptor Transaction = Rule\(\s*"(LEVEE004)",\s*"[^"]+",\s*"([^"]+)"/.exec(source);
	const format = /new\(id, title, \$"([^"]+)"/.exec(source);
	if (!descriptor || !format) throw new Error('Cannot read the transaction diagnostic from Descriptors.cs');
	return format[1].replace('{message}', descriptor[2].replace('{0}', 'DbContext.SaveChanges')).replace('{id}', descriptor[1]);
}
