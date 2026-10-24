import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { primaryNavItems } from '../../src/client-web/src/layout/Sidebar';
import AppKnowledgeTabs from '../../src/client-web/src/components/app-knowledge/AppKnowledgeTabs';

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const ts = requireFromClient('typescript') as typeof import('typescript');
const React = requireFromClient('react') as typeof import('react');
const { renderToStaticMarkup } = requireFromClient('react-dom/server') as typeof import('react-dom/server');
const { MemoryRouter, matchRoutes } = requireFromClient('react-router-dom') as typeof import('react-router-dom');
const reactGlobal = globalThis as typeof globalThis & { React: typeof React };
reactGlobal.React = React;

interface AppLayoutRoute {
  path: string;
  redirectTo?: string;
}

function test(_name: string, run: () => void) {
  run();
}

function findJsxAttribute(
  sourceFile: import('typescript').SourceFile,
  attributes: import('typescript').JsxAttributes,
  name: string,
) {
  return attributes.properties.find(
    property => ts.isJsxAttribute(property) && property.name.getText(sourceFile) === name,
  );
}

function readStringAttribute(
  sourceFile: import('typescript').SourceFile,
  attributes: import('typescript').JsxAttributes,
  name: string,
) {
  const attribute = findJsxAttribute(sourceFile, attributes, name);
  if (!attribute?.initializer) return null;
  if (ts.isStringLiteral(attribute.initializer)) return attribute.initializer.text;
  if (ts.isJsxExpression(attribute.initializer) && attribute.initializer.expression) {
    const expression = attribute.initializer.expression;
    return ts.isStringLiteral(expression) ? expression.text : null;
  }
  return null;
}

function readElementAttribute(
  sourceFile: import('typescript').SourceFile,
  attributes: import('typescript').JsxAttributes,
  name: string,
) {
  const attribute = findJsxAttribute(sourceFile, attributes, name);
  if (!attribute?.initializer || !ts.isJsxExpression(attribute.initializer)) return null;
  return attribute.initializer.expression ?? null;
}

function extractAppLayoutRoutes(): AppLayoutRoute[] {
  const appLayoutPath = path.join(process.cwd(), 'src/client-web/src/layout/AppLayout.tsx');
  const sourceFile = ts.createSourceFile(
    appLayoutPath,
    readFileSync(appLayoutPath, 'utf8'),
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TSX,
  );
  const routes: AppLayoutRoute[] = [];

  function visit(node: import('typescript').Node) {
    if (ts.isJsxSelfClosingElement(node) && node.tagName.getText(sourceFile) === 'Route') {
      const routePath = readStringAttribute(sourceFile, node.attributes, 'path');
      if (routePath) {
        const route: AppLayoutRoute = { path: routePath };
        const element = readElementAttribute(sourceFile, node.attributes, 'element');
        if (element && ts.isJsxSelfClosingElement(element) && element.tagName.getText(sourceFile) === 'Navigate') {
          route.redirectTo = readStringAttribute(sourceFile, element.attributes, 'to') ?? undefined;
        }
        routes.push(route);
      }
    }

    ts.forEachChild(node, visit);
  }

  visit(sourceFile);
  return routes;
}

function resolveAppLayoutPath(routes: AppLayoutRoute[], initialPath: string) {
  const routeObjects = routes.map(route => ({
    path: route.path,
    handle: route.redirectTo ? { redirectTo: route.redirectTo } : undefined,
  }));
  const seen = new Set<string>();
  let currentPath = initialPath;

  while (true) {
    if (seen.has(currentPath)) {
      throw new Error(`Redirect cycle while resolving ${initialPath}: ${[...seen, currentPath].join(' -> ')}`);
    }
    seen.add(currentPath);

    const matchedRoute = matchRoutes(routeObjects, currentPath)?.at(-1)?.route as
      | { handle?: { redirectTo?: string } }
      | undefined;
    if (!matchedRoute) return null;

    const redirectTo = matchedRoute.handle?.redirectTo;
    if (!redirectTo) return currentPath;
    currentPath = redirectTo;
  }
}

test('sidebar exposes app knowledge but not standalone classification pages', () => {
  const labels = primaryNavItems.map(item => item.label);

  assert.equal(labels.includes('App知识库'), true);
  assert.equal(labels.includes('分类管理'), false);
  assert.equal(labels.includes('分类树'), false);
});

test('app knowledge tabs include category tree as a secondary page', () => {
  const html = renderToStaticMarkup(
    React.createElement(
      MemoryRouter,
      null,
      React.createElement(AppKnowledgeTabs, { active: 'categories' })
    )
  );

  assert.equal(html.includes('App 列表'), true);
  assert.equal(html.includes('分类树'), true);
  assert.equal(html.includes('/app-knowledge-base/categories'), true);
});

test('app layout legacy knowledge routes resolve to current app knowledge pages', () => {
  const routes = extractAppLayoutRoutes();

  assert.equal(resolveAppLayoutPath(routes, '/pc-categories'), '/app-knowledge-base/categories');
  assert.equal(resolveAppLayoutPath(routes, '/pc-classification'), '/app-knowledge-base');
});
