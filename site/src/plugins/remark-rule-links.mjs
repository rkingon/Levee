import { visit, SKIP } from 'unist-util-visit';

// A diagnostic mentioned in a guide should lead straight to its explanation.
export default function remarkRuleLinks({ ids, base }) {
	const known = new Set(ids);
	return (tree, file) => {
		const self = /rules\/(levee\d{3})\.md$/.exec(file.path ?? '')?.[1]?.toUpperCase();
		visit(tree, (node, index, parent) => {
			if (node.type === 'heading' || node.type === 'link' || node.type === 'linkReference') return SKIP;
			if (node.type !== 'text' || !parent || index === undefined) return;
			const parts = node.value.split(/(LEVEE\d{3})/);
			if (parts.length === 1) return;
			const nodes = parts
				.filter((part) => part !== '')
				.map((part) =>
					known.has(part) && part !== self
						? { type: 'link', url: `${base}rules/${part.toLowerCase()}/`, children: [{ type: 'text', value: part }] }
						: { type: 'text', value: part },
				);
			parent.children.splice(index, 1, ...nodes);
			return [SKIP, index + nodes.length];
		});
	};
}
