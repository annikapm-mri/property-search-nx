import { useState } from 'react';
import { SearchBar } from './components/SearchBar';
import { PropertyCard } from './components/PropertyCard';
import { searchProperties, scrapeProperties } from './services/api';
import type { SearchResult } from './services/api';
import type { Property } from '@property-search/shared-contracts';
import './app.css';

function PropertySkeleton() {
  return (
    <div className="property-card skeleton">
      <div className="skeleton-line short" />
      <div className="skeleton-line" />
      <div className="skeleton-line medium" />
      <div className="skeleton-price" />
      <div className="skeleton-details" />
    </div>
  );
}

export function App() {
  const [keyword, setKeyword]       = useState('');
  const [dbResults, setDbResults]   = useState<SearchResult | null>(null);
  const [webResults, setWebResults] = useState<SearchResult | null>(null);
  const [loadingDb, setLoadingDb]   = useState(false);
  const [loadingWeb, setLoadingWeb] = useState(false);
  const [error, setError]           = useState('');

  const handleSearch = async (kw: string) => {
    if (!kw.trim()) return;
    setKeyword(kw);
    setDbResults(null);
    setWebResults(null);
    setError('');
    setLoadingDb(true);
    try {
      const data = await searchProperties(kw);
      setDbResults(data);
    } catch (e: any) {
      setError(e.message ?? 'Could not reach the API. Is the backend running?');
    } finally {
      setLoadingDb(false);
    }
  };

  const handleFindMore = async () => {
    if (!keyword) return;
    setLoadingWeb(true);
    setError('');
    try {
      const data = await scrapeProperties(keyword);
      setWebResults(data as any);
    } catch {
      setError('Web scraping failed. Try again later.');
    } finally {
      setLoadingWeb(false);
    }
  };

  return (
    <div className="app">
      <header>
        <h1>🏠 US Property Search</h1>
        <p>Search millions of US properties — powered by database + live web crawling</p>
      </header>

      <SearchBar onSearch={handleSearch} loading={loadingDb} />

      {error && <div className="error">{error}</div>}

      {dbResults?.interpretation && (
        <div className="claude-interpretation">
          <span>🧠 Understood: </span>
          <em>{dbResults.interpretation}</em>
        </div>
      )}

      {/* DATABASE SECTION */}
      {(loadingDb || dbResults) && (
        <section className="results-section">
          <div className="section-header database-header">
            <span className="section-icon">📦</span>
            <div>
              <h2>Database Results</h2>
              {dbResults && <p>{dbResults.total} properties found</p>}
            </div>
            <span className="source-badge database">From Cache</span>
          </div>

          <div className="property-grid">
            {loadingDb
              ? Array.from({ length: 6 }).map((_, i) => <PropertySkeleton key={i} />)
              : dbResults?.properties.map((p: Property) => (
                  <PropertyCard key={p.id} property={p} source="database" />
                ))}
          </div>

          {dbResults && !webResults && !loadingWeb && (
            <div className="find-more-wrapper">
              <button className="find-more-btn" onClick={handleFindMore}>
                🌐 Find More Online
              </button>
              <p>Search the live web for additional listings</p>
            </div>
          )}
        </section>
      )}

      {/* WEB SCRAPE SECTION */}
      {(loadingWeb || webResults) && (
        <section className="results-section">
          <div className="section-header web-header">
            <span className="section-icon">🌐</span>
            <div>
              <h2>Live Web Results</h2>
              {webResults && <p>{webResults.total} properties found online</p>}
            </div>
            <span className="source-badge web_scrape">Live Scrape</span>
          </div>

          <div className="property-grid">
            {loadingWeb
              ? Array.from({ length: 3 }).map((_, i) => <PropertySkeleton key={i} />)
              : webResults?.properties.length === 0
              ? (
                <div className="no-results">
                  <p>🔍 No additional listings found for "<strong>{keyword}</strong>"</p>
                </div>
              )
              : webResults?.properties.map((p: Property) => (
                  <PropertyCard key={p.id} property={p} source="web" />
                ))}
          </div>
        </section>
      )}
    </div>
  );
}

export default App;