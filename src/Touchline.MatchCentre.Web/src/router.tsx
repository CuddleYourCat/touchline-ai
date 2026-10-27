import { createBrowserRouter } from 'react-router';
import { ErrorPage } from './pages/ErrorPage';
import { Layout } from './Layout';
import { ClubPage } from './pages/ClubPage';
import { HomePage } from './pages/HomePage';
import { KickoffPage } from './pages/KickoffPage';
import { MatchPage } from './pages/MatchPage';
import { NotFoundPage } from './pages/NotFoundPage';
import { RecapPage } from './pages/RecapPage';
import { TeamEditPage } from './pages/TeamEditPage';
import { TeamsPage } from './pages/TeamsPage';

/** Path URLs (ADR-034); the Match Centre falls back to index.html for any of them. */
export const routes = [
  {
    element: <Layout />,
    // Outside the shell as a last resort; a page's own errors are caught below, inside the header.
    errorElement: <ErrorPage />,
    children: [
      {
        // A pathless route, so a page that throws shows the error page inside the normal header.
        errorElement: <ErrorPage />,
        children: [
          { path: '/', element: <HomePage /> },
          { path: '/matches/:id', element: <MatchPage /> },
          { path: '/matches/:id/recap', element: <RecapPage /> },
          { path: '/teams', element: <TeamsPage /> },
          { path: '/teams/new', element: <TeamEditPage /> },
          { path: '/teams/:id', element: <ClubPage /> },
          { path: '/teams/:id/edit', element: <TeamEditPage /> },
          { path: '/kickoff', element: <KickoffPage /> },
          { path: '*', element: <NotFoundPage /> },
        ],
      },
    ],
  },
];

export const router = createBrowserRouter(routes);
