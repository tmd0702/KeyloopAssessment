import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import './App.css'

type Document = { id: string; title: string; type: string; documentDate: string; source: 'Sales' | 'Service' }
type SourceStatus = 'IDLE' | 'PENDING' | 'SUCCESS' | 'FAILED'
type Source = 'Sales' | 'Service'
const api = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5100'
const dealershipId = import.meta.env.VITE_DEALERSHIP_ID ?? '42'
const sortDocuments = (documents: Document[]) => [...documents].sort((a, b) => Date.parse(b.documentDate) - Date.parse(a.documentDate) || a.source.localeCompare(b.source) || a.id.localeCompare(b.id))
const sourceLabel: Record<SourceStatus, string> = { IDLE: 'Not requested', PENDING: 'Retrieving', SUCCESS: 'Available', FAILED: 'Unavailable' }

function App() {
  const [vin, setVin] = useState('COMMERCIAL-001')
  const [documents, setDocuments] = useState<Document[]>([])
  const [selectedDocumentId, setSelectedDocumentId] = useState<string | null>(null)
  const [searchStatus, setSearchStatus] = useState('IDLE')
  const [salesStatus, setSalesStatus] = useState<SourceStatus>('IDLE')
  const [serviceStatus, setServiceStatus] = useState<SourceStatus>('IDLE')
  const [errorMessage, setErrorMessage] = useState<string | null>(null)
  const events = useRef<AbortController | null>(null)

  useEffect(() => () => events.current?.abort(), [])
  const search = async (event: FormEvent) => {
    event.preventDefault(); events.current?.abort()
    setDocuments([]); setSelectedDocumentId(null); setSearchStatus('SEARCHING'); setSalesStatus('PENDING'); setServiceStatus('PENDING'); setErrorMessage(null)
    try {
      const tokenResponse = await fetch(`${api}/api/v1/auth/demo-token`)
      if (!tokenResponse.ok) throw new Error('Authentication token was not issued.')
      const { accessToken } = await tokenResponse.json() as { accessToken: string }
      const controller = new AbortController(); events.current = controller
      const response = await fetch(`${api}/api/v1/vehicles/${encodeURIComponent(vin)}/documents/stream`, { headers: { Authorization: `Bearer ${accessToken}`, 'X-Dealership-Id': dealershipId }, signal: controller.signal })
      if (!response.ok || !response.body) throw new Error('The document stream could not be opened.')
      const update = (data: string, failed: boolean) => {
      const payload = JSON.parse(data) as { source: 'SALES' | 'SERVICE'; documents?: Document[] }
      payload.source === 'SALES' ? setSalesStatus(failed ? 'FAILED' : 'SUCCESS') : setServiceStatus(failed ? 'FAILED' : 'SUCCESS')
      const incoming = payload.documents
      if (incoming) setDocuments(current => sortDocuments([...current, ...incoming]))
    }
      const reader = response.body.getReader(); const decoder = new TextDecoder(); let buffer = ''
      for (;;) {
        const { value, done } = await reader.read(); if (done) break
        buffer += decoder.decode(value, { stream: true })
        const frames = buffer.split('\n\n'); buffer = frames.pop() ?? ''
        for (const frame of frames) {
          const name = frame.match(/^event: (.+)$/m)?.[1]; const data = frame.match(/^data: (.+)$/m)?.[1]
          if (!name || !data) continue
          if (name === 'source.completed') update(data, false)
          if (name === 'source.failed') update(data, true)
          if (name === 'search.completed') setSearchStatus((JSON.parse(data) as { status: string }).status.toUpperCase())
        }
      }
    } catch {
      setSearchStatus('FAILED'); setSalesStatus('IDLE'); setServiceStatus('IDLE'); setErrorMessage('Authentication could not be completed. Try again later.')
    }
  }
  const selected = documents.find(document => document.id === selectedDocumentId)
  const isLoading = searchStatus === 'SEARCHING'
  const completedSources = [salesStatus, serviceStatus].filter(status => status === 'SUCCESS' || status === 'FAILED').length
  const sourceStatus = (source: Source, status: SourceStatus) => <article className={`source-card ${status.toLowerCase()}`}><span className="status-dot" aria-hidden="true" /><div><strong>{source}</strong><small>{sourceLabel[status]}</small></div>{status === 'PENDING' && <span className="spinner" aria-label={`${source} loading`} />}</article>
  return <main className="workspace"><header className="topbar"><div className="brand">KEYLOOP</div><div className="environment">Unified Documents <span>Operational</span></div></header><section className="page-heading"><div><p className="breadcrumb">Vehicles / Document search</p><h1>Vehicle documents</h1><p>Retrieve Sales and Service metadata for a single vehicle VIN.</p></div><div className="result-count">{documents.length}<span>documents</span></div></section><section className="search-panel"><form onSubmit={search}><div className="field"><label htmlFor="vin">Vehicle identification number</label><input id="vin" value={vin} onChange={event => setVin(event.target.value)} autoComplete="off" spellCheck="false" /></div><button type="submit" disabled={isLoading}>{isLoading ? 'Searching' : 'Search documents'}</button></form>{errorMessage && <p className="search-error" role="alert">{errorMessage}</p>}<div className="source-grid">{sourceStatus('Sales', salesStatus)}{sourceStatus('Service', serviceStatus)}</div>{isLoading && <div className="progress" aria-label={`Completed ${completedSources} of 2 source searches`}><span style={{ width: `${completedSources * 50}%` }} /></div>}</section><section className="content-grid"><section className="documents-panel"><div className="panel-heading"><div><h2>Documents</h2><p>{isLoading ? 'Results will appear as each source completes.' : searchStatus === 'PARTIAL' ? 'Partial results are available.' : 'Sorted by newest document date.'}</p></div><span className={`overall-status ${searchStatus.toLowerCase()}`}>{searchStatus}</span></div><div className="table-shell"><table><thead><tr><th>Date</th><th>Document</th><th>Type</th><th>Source</th></tr></thead><tbody>{documents.map(document => <tr key={document.id} className={selectedDocumentId === document.id ? 'selected' : ''} onClick={() => setSelectedDocumentId(document.id)}><td>{new Intl.DateTimeFormat('en-GB', { dateStyle: 'medium' }).format(new Date(document.documentDate))}</td><td><strong>{document.title}</strong><small>{document.id}</small></td><td>{document.type.replaceAll('_', ' ')}</td><td><span className={`source-tag ${document.source.toLowerCase()}`}>{document.source}</span></td></tr>)}{isLoading && documents.length === 0 && Array.from({ length: 6 }, (_, index) => <tr className="skeleton-row" key={index}><td><span /></td><td><span /></td><td><span /></td><td><span /></td></tr>)}</tbody></table>{!isLoading && documents.length === 0 && <div className="empty-state"><strong>No documents to show</strong><p>{searchStatus === 'FAILED' ? 'Neither provider returned a usable result.' : 'Enter a supported VIN and begin a search.'}</p></div>}</div></section><aside className="details-panel"><p className="panel-label">Selection</p><h2>Document details</h2>{selected ? <dl><div><dt>Title</dt><dd>{selected.title}</dd></div><div><dt>Document ID</dt><dd>{selected.id}</dd></div><div><dt>Source</dt><dd>{selected.source}</dd></div><div><dt>Date</dt><dd>{new Intl.DateTimeFormat('en-GB', { dateStyle: 'long' }).format(new Date(selected.documentDate))}</dd></div></dl> : <div className="details-empty"><span aria-hidden="true">+</span><p>Select a row to inspect document metadata.</p></div>}</aside></section></main>
}

export default App
