# Levee documentation site

From the repository root, use Node.js 24 and npm.

```sh
cd site
npm ci
npm run dev
```

Open <http://localhost:4321/Levee/>. The rules index is at `/Levee/rules/` and each diagnostic has a stable URL such as `/Levee/rules/levee004/`.

`npm run build` builds the static site in `site/dist/`. Run `npm run preview` to serve that build locally, including its search index.

Edit the landing page in `src/pages/index.astro` and its handler example in `src/components/HomeHero.astro`. The example reads the diagnostic from the analyzer. The Astro integration generates the overview from the repository README, the documentation pages from `docs/*.md`, and the rules index and individual pages from `docs/rules.md`. It regenerates the ignored `src/content/docs/` directory when dev or build starts.

Select **GitHub Actions** as the repository’s Pages source. The site workflow publishes the build from main to <https://rkingon.github.io/Levee/>.
