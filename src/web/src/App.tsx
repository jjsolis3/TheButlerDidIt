import { Navigate, Route, Routes } from 'react-router'
import { ConfirmEmail, ConfirmEmailChange, ForgotPassword, ResetPassword } from './pages/Account'
import AdminAi from './pages/AdminAi'
import AdminHosts from './pages/AdminHosts'
import EscapeHowToPlay from './pages/EscapeHowToPlay'
import EscapeLanding from './pages/EscapeLanding'
import EscapeRecap from './pages/EscapeRecap'
import EscapeRoomEditor from './pages/EscapeRoomEditor'
import Home from './pages/Home'
import HowToPlay from './pages/HowToPlay'
import Join from './pages/Join'
import Login from './pages/Login'
import NewParty from './pages/NewParty'
import PassAndPlay from './pages/PassAndPlay'
import MyAccount from './pages/MyAccount'
import MyEscapeRooms from './pages/MyEscapeRooms'
import Settings from './pages/Settings'
import Insights from './pages/Insights'
import MyMysteries from './pages/MyMysteries'
import MysteryLanding from './pages/MysteryLanding'
import Play from './pages/Play'
import ScenarioEditor from './pages/ScenarioEditor'
import Recap from './pages/Recap'
import Remote from './pages/Remote'
import Stage from './pages/Stage'
import Watch from './pages/Watch'

// Page map. The same URLs work in production because ASP.NET Core falls back to
// index.html for any path it doesn't recognise (MapFallbackToFile in Program.cs).
export default function App() {
  return (
    <Routes>
      <Route path="/" element={<Home />} />
      {/* Each game's own front door. (/mysteries is the host's own library, "My mysteries".) */}
      <Route path="/mystery" element={<MysteryLanding />} />
      <Route path="/escape" element={<EscapeLanding />} />
      <Route path="/login" element={<Login />} />
      <Route path="/host/new" element={<NewParty />} />
      <Route path="/admin/ai" element={<AdminAi />} />
      <Route path="/admin/hosts" element={<AdminHosts />} />
      <Route path="/forgot-password" element={<ForgotPassword />} />
      <Route path="/reset-password" element={<ResetPassword />} />
      <Route path="/confirm-email" element={<ConfirmEmail />} />
      <Route path="/account" element={<MyAccount />} />
      <Route path="/settings" element={<Settings />} />
      <Route path="/insights/:kind/:id" element={<Insights />} />
      <Route path="/account/confirm-email" element={<ConfirmEmailChange />} />
      <Route path="/recap/:slug" element={<Recap />} />
      <Route path="/escape/recap/:slug" element={<EscapeRecap />} />
      <Route path="/escape/rooms" element={<MyEscapeRooms />} />
      <Route path="/escape/rooms/:id" element={<EscapeRoomEditor />} />
      <Route path="/mysteries" element={<MyMysteries />} />
      <Route path="/how-to-play" element={<HowToPlay />} />
      <Route path="/how-to-play/escape" element={<EscapeHowToPlay />} />
      <Route path="/mysteries/:id" element={<ScenarioEditor />} />
      <Route path="/join" element={<Join />} />
      <Route path="/join/:code" element={<Join />} />
      <Route path="/stage/:code" element={<Stage />} />
      <Route path="/remote/:code" element={<Remote />} />
      <Route path="/play/:code" element={<Play />} />
      <Route path="/watch/:code" element={<Watch />} />
      <Route path="/pass/:code" element={<PassAndPlay />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
