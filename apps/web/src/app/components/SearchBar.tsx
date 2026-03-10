import { useState } from 'react';
import { validateSearchRequest } from '@property-search/shared-validation';

interface Props {
  onSearch: (keyword: string) => void;
  loading: boolean;
}

export function SearchBar({ onSearch, loading }: Props) {
  const [value, setValue] = useState('');
  const [errors, setErrors] = useState<string[]>([]);

  const handleSearch = () => {
    const result = validateSearchRequest({ keyword: value });
    if (!result.valid) { setErrors(result.errors); return; }
    setErrors([]);
    onSearch(value);
  };

  return (
    <div className="search-bar-wrapper">
      <div className="search-bar">
        <input
          type="text"
          placeholder="e.g. '3 bed house austin under 2000'"
          value={value}
          onChange={e => { setValue(e.target.value); setErrors([]); }}
          onKeyDown={e => e.key === 'Enter' && handleSearch()}
          className={errors.length > 0 ? 'input-error' : ''}
        />
        <button onClick={handleSearch} disabled={loading}>
          {loading ? 'Searching...' : '🔍 Search'}
        </button>
      </div>
      {errors.map((err, i) => (
        <div key={i} className="validation-error">⚠️ {err}</div>
      ))}
    </div>
  );
}