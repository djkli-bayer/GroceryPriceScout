import { useRoute } from './router'
import NewRun from './pages/NewRun'
import RunPage from './pages/RunPage'
import RunsList from './pages/RunsList'
import Settings from './pages/Settings'

export default function App() {
  const route = useRoute()
  const tab = route.name === 'run' ? 'runs' : route.name

  return (
    <div className="app">
      <main>
        {route.name === 'new' && <NewRun />}
        {route.name === 'runs' && <RunsList />}
        {route.name === 'run' && <RunPage key={route.id} id={route.id} />}
        {route.name === 'settings' && <Settings />}
      </main>
      <nav className="tabs" aria-label="Main">
        <a href="#/" className={tab === 'new' ? 'active' : ''}>New</a>
        <a href="#/runs" className={tab === 'runs' ? 'active' : ''}>Runs</a>
        <a href="#/settings" className={tab === 'settings' ? 'active' : ''}>Settings</a>
      </nav>
    </div>
  )
}
