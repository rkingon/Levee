// @ts-check
import { defineConfig, fontProviders } from 'astro/config';
import { unified } from '@astrojs/markdown-remark';
import starlight from '@astrojs/starlight';
import { base, repo, rules } from './src/lib/source.mjs';
import { leveeDocs } from './src/lib/sync.mjs';
import remarkRuleLinks from './src/plugins/remark-rule-links.mjs';

export default defineConfig({
	site: 'https://rkingon.github.io',
	base,
	// Diagnostic help links need a stable canonical URL.
	trailingSlash: 'always',
	fonts: [
		{
			name: 'DM Sans',
			cssVariable: '--sl-font',
			provider: fontProviders.local(),
			display: 'optional',
			options: { variants: [{
				src: ['@fontsource-variable/dm-sans/files/dm-sans-latin-wght-normal.woff2'],
				weight: '100 1000',
				style: 'normal',
			}] },
		},
		{
			name: 'Barlow Condensed',
			cssVariable: '--display',
			provider: fontProviders.local(),
			display: 'optional',
			options: { variants: [{
				src: ['@fontsource/barlow-condensed/files/barlow-condensed-latin-600-normal.woff2'],
				weight: 600,
				style: 'normal',
			}] },
		},
	],
	markdown: {
		processor: unified({ remarkPlugins: [[remarkRuleLinks, { ids: rules().list.map((r) => r.id), base }]] }),
	},
	integrations: [
		leveeDocs(),
		starlight({
			title: 'Levee',
			description: 'The .NET framework that keeps your AI in check.',
			social: [{ icon: 'github', label: 'GitHub', href: repo }],
			components: {
				Head: './src/components/Head.astro',
				Hero: './src/components/HomeHero.astro',
			},
			customCss: ['./src/styles/custom.css'],
			sidebar: [
				{ label: 'Start', items: ['docs', 'overview', 'principles'] },
				{ label: 'Guides', items: ['postgres', 'jobs', 'subscriptions', 'mcp'] },
				{ label: 'Rules', collapsed: true, items: [{ autogenerate: { directory: 'rules' } }] },
				{ label: 'Reference', items: ['decisions', 'benchmarks'] },
			],
		}),
	],
});
