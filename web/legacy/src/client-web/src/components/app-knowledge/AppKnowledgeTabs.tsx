import { Link } from 'react-router-dom';

interface Props {
  active: 'apps' | 'categories';
}

const tabs = [
  { id: 'apps' as const, label: 'App 列表', path: '/app-knowledge-base' },
  { id: 'categories' as const, label: '分类树', path: '/app-knowledge-base/categories' },
];

export default function AppKnowledgeTabs({ active }: Props) {
  return (
    <nav className="flex flex-wrap gap-2" aria-label="App 知识库导航">
      {tabs.map(tab => (
        <Link
          key={tab.id}
          to={tab.path}
          aria-current={tab.id === active ? 'page' : undefined}
          className={`rounded-lg px-3 py-1.5 text-sm font-medium transition-colors ${
            tab.id === active
              ? 'bg-blue-600 text-white'
              : 'border border-slate-200 bg-white text-slate-600 hover:bg-slate-50'
          }`}
        >
          {tab.label}
        </Link>
      ))}
    </nav>
  );
}
