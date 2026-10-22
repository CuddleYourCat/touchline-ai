import { useParams } from 'react-router';
import { Placeholder } from './Placeholder';

/** `/matches/:id`: one match, live or finished. */
export function MatchPage() {
  const { id } = useParams();
  return (
    <Placeholder title="Match">
      The live view for match <code className="data">{id}</code> will appear here.
    </Placeholder>
  );
}
