import { Navigate, Route, Routes } from 'react-router'
import AdminAi from './pages/AdminAi'
import Home from './pages/Home'
import Join from './pages/Join'
import Login from './pages/Login'
import NewParty from './pages/NewParty'
import PassAndPlay from './pages/PassAndPlay'
import Play from './pages/Play'
import Stage from './pages/Stage'

// Page map. The same URLs work in production because ASP.NET Core falls back to
// index.html for any path it doesn't recognise (MapFallbackToFile in Program.cs).
export default function App() {
  return (
    <Routes>
      <Route path="/" element={<Home />} />
      <Route path="/login" element={<Login />} />
      <Route path="/host/new" element={<NewParty />} />
      <Route path="/admin/ai" element={<AdminAi />} />
      <Route path="/join" element={<Join />} />
      <Route path="/join/:code" element={<Join />} />
      <Route path="/stage/:code" element={<Stage />} />
      <Route path="/play/:code" element={<Play />} />
      <Route path="/pass/:code" element={<PassAndPlay />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
