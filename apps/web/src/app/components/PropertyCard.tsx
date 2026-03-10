import type { Property } from '../services/api';

interface Props {
  property: Property;
  source: 'database' | 'web';
}

export function PropertyCard({ property, source }: Props) {
  return (
    <div className={`property-card ${source === 'web' ? 'web-card' : ''}`}>
      <div className="card-top-row">
        <div className="property-type">{property.type || 'Property'}</div>
        {source === 'web' && <span className="live-badge">🌐 Live</span>}
      </div>
      <h3>{property.region}</h3>
      <p className="location">{property.state?.toUpperCase()}</p>
      <div className="price">
        {Number(property.price) > 0
          ? `$${Number(property.price).toLocaleString()}/mo`
          : 'Price not listed'}
      </div>
      <div className="details">
        <span>🛏 {property.beds} bed</span>
        <span>🚿 {property.baths} bath</span>
        <span>📐 {property.sqFeet} sqft</span>
      </div>
      {property.imageUrl && (
        <img
          src={property.imageUrl}
          alt="Property"
          onError={e => (e.currentTarget.style.display = 'none')}
        />
      )}
      <p className="description">{property.description}</p>
      {source === 'web' && (
  <span className="live-badge">🏠 Craigslist Live</span>
)}
      {property.url && (
        <a href={property.url} target="_blank" rel="noopener noreferrer">
          View Listing →
        </a>
      )}
    </div>
  );
}