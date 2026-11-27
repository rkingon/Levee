import fs from 'node:fs';
import path from 'node:path';
import { base, read, repo, root, rules } from './source.mjs';

const out = path.join(root, 'site/src/content/docs');

const pages = [
	{ source: 'README.md', slug: 'overview', title: 'Overview' },
	...fs.readdirSync(path.join(root, 'docs'))
		.filter((file) => file.endsWith('.md') && file !== 'rules.md')
		.map((file) => ({
			source: `docs/${file}`,
			slug: file === 'README.md' ? 'docs' : path.basename(file, '.md'),
			title: file === 'README.md' ? 'Documentation' : undefined,
		})),
];

const slugOf = new Map([...pages.map((p) => [p.source, p.slug + '/']), ['docs/rules.md', 'rules/']]);

const sources = [...pages.map((p) => p.source), 'docs/rules.md', 'src/Levee.Analyzers/Descriptors.cs'];

// Repository links should keep readers on the rendered page when one exists.
function relink(markdown, source) {
	let fenced = false;
	return markdown
		.split('\n')
		.map((line) => {
			if (/^\s*```/.test(line)) fenced = !fenced;
			if (fenced) return line;
			return line.replace(/\]\(([^)\s]+)\)/g, (whole, target) => {
				if (/^(https?:|mailto:|#)/.test(target)) return whole;
				const [file, hash = ''] = target.split(/(?=#)/);
				const resolved = path.posix.normalize(path.posix.join(path.posix.dirname(source), file));
				const slug = slugOf.get(resolved);
				return `](${slug ? base + slug : `${repo}/blob/main/${resolved}`}${hash})`;
			});
		})
		.join('\n');
}

function frontmatter(fields) {
	return '---\n' + Object.entries(fields).map(([k, v]) => `${k}: ${JSON.stringify(v)}`).join('\n') + '\n---\n\n';
}

function write(slug, fields, body) {
	const file = path.join(out, slug + '.md');
	fs.mkdirSync(path.dirname(file), { recursive: true });
	fs.writeFileSync(file, frontmatter(fields) + body.trim() + '\n');
}

const edit = (source) => `${repo}/edit/main/${source}`;

function sync() {
	fs.rmSync(out, { recursive: true, force: true });

	for (const { source, slug, title } of pages) {
		const markdown = read(source);
		const h1 = /^# (.+)$/m.exec(markdown);
		if (!h1) throw new Error(`${source} has no "# Title".`);
		write(slug, { title: title ?? h1[1], editUrl: edit(source) }, relink(markdown.replace(h1[0], ''), source));
	}

	const { intro, list } = rules();
	const table = ['| Rule | Title |', '|---|---|', ...list.map((r) => `| [${r.id}](${base}rules/${r.slug}/) | ${r.title} |`)];
	write(
		'rules/index',
		{ title: 'Rules', editUrl: edit('docs/rules.md'), sidebar: { label: 'All rules', order: 0 } },
		relink(intro, 'docs/rules.md') + '\n\n' + table.join('\n'),
	);

	list.forEach((rule, i) => {
		write(
			`rules/${rule.slug}`,
			{ title: `${rule.id}: ${rule.title}`, editUrl: edit('docs/rules.md'), sidebar: { label: rule.id, order: i + 1 } },
			relink(rule.body, 'docs/rules.md'),
		);
	});
}

export function leveeDocs() {
	return {
		name: 'levee-docs',
		hooks: {
			'astro:config:setup': ({ addWatchFile, logger }) => {
				sync();
				for (const source of sources) addWatchFile(path.join(root, source));
				logger.info(`Generated src/content/docs from the repository Markdown.`);
			},
		},
	};
}
