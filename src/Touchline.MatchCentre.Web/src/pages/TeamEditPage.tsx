import { useParams } from 'react-router';
import { Placeholder } from './Placeholder';

/** `/teams/:id/edit`: the team builder (ADR-031). */
export function TeamEditPage() {
  const { id } = useParams();
  return (
    <Placeholder title="Edit team">
      The team builder for <code className="data">{id}</code> will appear here.
    </Placeholder>
  );
}
