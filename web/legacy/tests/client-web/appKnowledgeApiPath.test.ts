import assert from 'node:assert/strict';
import { appKnowledgeApiPaths } from '../../src/client-web/src/api/appKnowledge';

assert.equal(appKnowledgeApiPaths.apps(), '/pc/app-knowledge/apps');
assert.equal(appKnowledgeApiPaths.apps('code'), '/pc/app-knowledge/apps?search=code');
assert.equal(appKnowledgeApiPaths.appContexts('app-1'), '/pc/app-knowledge/apps/app-1/contexts');
assert.equal(appKnowledgeApiPaths.suggestionPreview('suggestion-1'), '/pc/app-knowledge/suggestions/suggestion-1/preview');
assert.equal(appKnowledgeApiPaths.suggestionApply('suggestion-1'), '/pc/app-knowledge/suggestions/suggestion-1/apply');
