import React, { useState } from 'react';

const ReportPage: React.FC = () => {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState(false);

  // Вычисляем вчерашний день в формате YYYY-MM-DD для ограничения календаря
  const yesterday = new Date();
  yesterday.setDate(yesterday.getDate() - 1);
  const maxDateString = yesterday.toISOString().split('T')[0];

  // Состояния для выбранных дат (по умолчанию — последние 7 дней)
  const [fromDate, setFromDate] = useState(() => {
    const d = new Date();
    d.setDate(d.getDate() - 7);
    return d.toISOString().split('T')[0];
  });
  const [toDate, setToDate] = useState(maxDateString);

  const downloadReport = async () => {
    try {
      setLoading(true);
      setError(null);
      setSuccess(false);

      // Формируем URL с query-параметрами дат
      const url = `http://localhost:5000/api/proxy/reports?fromDate=${fromDate}&toDate=${toDate}`;

      const response = await fetch(url, {
        method: 'GET',
        credentials: 'include' 
      });

      if (response.status === 401) {
        window.location.href = 'http://localhost:5000/api/auth/login';
        return;
      }

      if (!response.ok) {
        // Если бэкенд вернул 400 или 404 с текстом ошибки
        const textError = await response.text();
        throw new Error(textError || `Ошибка сервера: ${response.status}`);
      }

      // ИСПРАВЛЕНО: Читаем ответ как JSON, а не как Blob-объект
      const data = await response.json();
      
      if (!data || !data.url) {
        throw new Error('Служба аналитики вернула некорректный формат данных (отсутствует ссылка).');
      }

      // Извлекаем детерминированную ссылку на CDN (Nginx) из JSON-ответа
      const cdnDownloadUrl = data.url;
      console.log(`[BFF Cache Status] Файл получен из кэша S3: ${data.fromCache}`);

      // Создаем виртуальную ссылку в DOM для скачивания файла браузером напрямую из CDN
      const link = document.createElement('a');
      link.href = cdnDownloadUrl;
      
      // Выделяем имя файла из URL (например, report_20260524_to_20260530.xlsx)
      const fileName = cdnDownloadUrl.substring(cdnDownloadUrl.lastIndexOf('/') + 1);
      link.setAttribute('download', fileName);
      
      document.body.appendChild(link);
      link.click();
      
      // Подчищаем за собой DOM-дерево
      link.parentNode?.removeChild(link);
      
      setSuccess(true);
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
        
        {/* Блок выбора периода */}
        <div className="flex gap-4 mb-6">
          <div className="flex flex-col flex-1">
            <label className="text-sm font-medium text-gray-600 mb-1">С даты:</label>
            <input 
              type="date" 
              value={fromDate}
              max={maxDateString}
              onChange={(e) => setFromDate(e.target.value)}
              className="p-2 border rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div className="flex flex-col flex-1">
            <label className="text-sm font-medium text-gray-600 mb-1">По дату (включительно):</label>
            <input 
              type="date" 
              value={toDate}
              max={maxDateString}
              onChange={(e) => setToDate(e.target.value)}
              className="p-2 border rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
        </div>

        <button
          onClick={downloadReport}
          disabled={loading}
          className={`w-full px-4 py-2 bg-blue-500 text-white rounded hover:bg-blue-600 transition-colors ${
            loading ? 'opacity-50 cursor-not-allowed' : ''
          }`}
        >
          {loading ? 'Generating Excel...' : 'Download Excel Report'}
        </button>

        {error && (
          <div className="mt-4 p-4 bg-red-100 text-red-700 rounded-md text-sm">
            {error}
          </div>
        )}

        {success && (
          <div className="mt-4 p-4 bg-green-100 text-green-700 rounded-md text-sm">
            Отчет за период с {fromDate} по {toDate} успешно сохранен!
          </div>
        )}
      </div>
    </div>
  );
};

export default ReportPage;