import React, { useState } from 'react';

const ReportPage: React.FC = () => {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState(false); // Добавим статус успешного скачивания

  const downloadReport = async () => {
    try {
      setLoading(true);
      setError(null);
      setSuccess(false);

      // Стучимся на прокси-эндпоинт бэкенда (BFF)
      const response = await fetch('http://localhost:5000/api/proxy/reports', {
        method: 'GET',
        credentials: 'include' // Передает куку BIONIC_SESSION
      });

      if (response.status === 401) {
        window.location.href = 'http://localhost:5000/api/auth/login';
        return;
      }

      if (!response.ok) {
        throw new Error(`Ошибка сервера: ${response.status}`);
      }

      // 1. Получаем ответ в виде бинарного Blob (а не json)
      const blob = await response.blob();

      // 2. Создаем временную URL-ссылку на этот Blob в памяти браузера
      const downloadUrl = window.URL.createObjectURL(blob);

      // 3. Создаем невидимый тег <a> для программного клика
      const link = document.createElement('a');
      link.href = downloadUrl;
      
      // Имя файла (браузер подхватит его из бэкенда, либо можно захардкодить свое)
      link.setAttribute('download', `analytics_report_${new Date().toISOString().slice(0,10)}.xlsx`);
      
      // Добавляем в документ, кликаем и удаляем
      document.body.appendChild(link);
      link.click();
      link.parentNode?.removeChild(link);

      // 4. Освобождаем память, удаляя временную ссылку
      window.URL.revokeObjectURL(downloadUrl);
      
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
        
        <button
          onClick={downloadReport}
          disabled={loading}
          className={`px-4 py-2 bg-blue-500 text-white rounded hover:bg-blue-600 transition-colors ${
            loading ? 'opacity-50 cursor-not-allowed' : ''
          }`}
        >
          {loading ? 'Generating Excel...' : 'Download Excel Report'}
        </button>

        {error && (
          <div className="mt-4 p-4 bg-red-100 text-red-700 rounded-md">
            {error}
          </div>
        )}

        {success && (
          <div className="mt-4 p-4 bg-green-100 text-green-700 rounded-md">
            Отчет успешно сгенерирован и сохранен в Загрузки!
          </div>
        )}
      </div>
    </div>
  );
};

export default ReportPage;