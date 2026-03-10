export interface ValidationResult {
  valid: boolean;
  errors: string[];
}

export function validateKeyword(keyword: string): ValidationResult {
  const errors: string[] = [];
  if (!keyword || keyword.trim().length === 0)
    errors.push('Keyword cannot be empty');
  if (keyword.trim().length < 2)
    errors.push('Keyword must be at least 2 characters');
  if (keyword.length > 200)
    errors.push('Keyword too long (max 200 characters)');
  if (/[<>{}]/.test(keyword))
    errors.push('Keyword contains invalid characters');
  return { valid: errors.length === 0, errors };
}

export function validateSearchRequest(request: {
  keyword: string;
  minPrice?: number;
  maxPrice?: number;
  minBeds?: number;
}): ValidationResult {
  const errors: string[] = [];

  const kwResult = validateKeyword(request.keyword);
  errors.push(...kwResult.errors);

  if (request.minPrice !== undefined && request.minPrice < 0)
    errors.push('Minimum price cannot be negative');
  if (request.maxPrice !== undefined && request.maxPrice < 0)
    errors.push('Maximum price cannot be negative');
  if (request.minPrice && request.maxPrice && request.minPrice > request.maxPrice)
    errors.push('Minimum price cannot exceed maximum price');
  if (request.minBeds !== undefined && request.minBeds < 0)
    errors.push('Beds cannot be negative');

  return { valid: errors.length === 0, errors };
}

export const PROPERTY_TYPES = [
  'apartment', 'house', 'condo', 'townhouse', 'flat', 'studio'
] as const;

export const US_STATES = [
  'al','ak','az','ar','ca','co','ct','de','fl','ga',
  'hi','id','il','in','ia','ks','ky','la','me','md',
  'ma','mi','mn','ms','mo','mt','ne','nv','nh','nj',
  'nm','ny','nc','nd','oh','ok','or','pa','ri','sc',
  'sd','tn','tx','ut','vt','va','wa','wv','wi','wy'
] as const;