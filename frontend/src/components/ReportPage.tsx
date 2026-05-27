import React, { useState } from 'react';

const ReportPage: React.FC = () => {
  const [loading, setLoading] = useState(false);
  const [reports, setReports] = useState<any>(null);
  const [error, setError] = useState<string | null>(null);

  const downloadReport = async () => {
    try {
      setLoading(true);
      setError(null);

      // Стучимся на прокси-эндпоинт нашего бэкенда (BFF)
      const response = await fetch('http://localhost:5000/api/proxy/reports', {
        method: 'GET',
        // КРИТИЧЕСКИ ВАЖНО: передает куку BIONIC_SESSION в запросе к бэкенду
        credentials: 'include' 
      });

      if (response.status === 401) {
        // Если сессия протухла в процессе, отправляем пользователя на перелогин
        window.location.href = 'http://localhost:5000/api/auth/login';
        return;
      }

      if (!response.ok) {
        throw new Error(`Ошибка сервера: ${response.status}`);
      }

      const data = await response.json();
      setReports(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Произошла ошибка при загрузке отчетов');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="flex flex-col items-center justify-center min-h-screen bg-gray-100">
      <div className="p-8 bg-white rounded-lg shadow-md w-full max-w-2xl">
        <h1 className="text-2xl font-bold mb-6 text-gray-800">Usage Reports</h1>
        
        <button
          onClick={downloadReport}
          disabled={loading}
          className={`px-4 py-2 bg-blue-500 text-white rounded hover:bg-blue-600 transition-colors ${
            loading ? 'opacity-50 cursor-not-allowed' : ''
          }`}
        >
          {loading ? 'Generating Report...' : 'Download Report'}
        </button>

        {error && (
          <div className="mt-4 p-4 bg-red-100 text-red-700 rounded-md">
            {error}
          </div>
        )}

        {reports && (
          <div className="mt-6 p-4 bg-gray-50 rounded-md border border-gray-200">
            <h3 className="font-semibold mb-2 text-gray-700">Полученные данные:</h3>
            <pre className="text-xs overflow-auto max-h-60 bg-gray-900 text-green-400 p-3 rounded">
              {JSON.stringify(reports, null, 2)}
            </pre>
          </div>
        )}
      </div>
    </div>
  );
};

export default ReportPage;