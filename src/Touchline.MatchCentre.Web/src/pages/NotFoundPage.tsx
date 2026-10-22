import { Link } from 'react-router';
import { Placeholder } from './Placeholder';

/** Any path the router doesn't know. */
export function NotFoundPage() {
  return (
    <Placeholder title="Page not found">
      There's nothing here. <Link to="/">Back to the matches</Link>.
    </Placeholder>
  );
}
