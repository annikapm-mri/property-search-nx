export interface Property {
  id: string;
  url: string;
  region: string;
  price: number;
  type: string;
  sqFeet: number;
  beds: number;
  baths: number;
  description: string;
  state: string;
  lat?: number;
  long?: number;
  imageUrl: string;
  interpretation?: string;
}

export interface SearchRequest {
  keyword: string;
  minPrice?: number;
  maxPrice?: number;
  minBeds?: number;
  type?: string;
  state?: string;
  page?: number;
  pageSize?: number;
}

export interface SearchResponse {
  source: DataSource;
  properties: Property[];
  total: number;
  page: number;
  pageSize: number;
  interpretation: string;
}

export interface ScrapeResponse {
  source: DataSource;
  properties: Property[];
  total: number;
  page: number;
  pageSize: number;
  interpretation: string;
}

export type DataSource = 'database' | 'web_scrape' | 'not_found';

export const API_ENDPOINTS = {
  search: '/api/property/search',
  scrape: '/api/property/scrape',
  health: '/api/health'
} as const;

export interface ApiError {
  message: string;
  statusCode: number;
  timestamp: string;
}