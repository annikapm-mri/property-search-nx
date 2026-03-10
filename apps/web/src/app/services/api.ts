import type {
  SearchResponse,
  ScrapeResponse,
  SearchRequest
} from '@property-search/shared-contracts';
import { API_ENDPOINTS } from '@property-search/shared-contracts';
import { validateSearchRequest } from '@property-search/shared-validation';

export type { SearchResponse as SearchResult };
export type { Property } from '@property-search/shared-contracts';

const BASE_URL = 'http://localhost:5218';
const cache = new Map<string, SearchResponse>();

export async function searchProperties(
  keyword: string,
  filters?: Partial<SearchRequest>
): Promise<SearchResponse> {
  const validation = validateSearchRequest({ keyword, ...filters });
  if (!validation.valid) throw new Error(validation.errors[0]);

  const key = `${keyword}:${JSON.stringify(filters)}`;
  if (cache.has(key)) return cache.get(key)!;

  const params = new URLSearchParams({ keyword, ...(filters as any) });
  const res = await fetch(`${BASE_URL}${API_ENDPOINTS.search}?${params}`);
  if (!res.ok) throw new Error('Search failed');

  const data: SearchResponse = await res.json();
  cache.set(key, data);
  return data;
}

export async function scrapeProperties(keyword: string): Promise<ScrapeResponse> {
  const validation = validateSearchRequest({ keyword });
  if (!validation.valid) throw new Error(validation.errors[0]);

  const res = await fetch(
    `${BASE_URL}${API_ENDPOINTS.scrape}?keyword=${encodeURIComponent(keyword)}`
  );
  if (!res.ok) throw new Error('Scrape failed');
  return res.json();
}
export async function recommendProperties(keyword: string) {
  const res = await fetch(
    `${BASE_URL}/api/property/recommend?keyword=${encodeURIComponent(keyword)}&limit=5`
  );
  if (!res.ok) throw new Error('Recommendation failed');
  return res.json();
}