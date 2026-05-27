import React, { useEffect, useState } from 'react';
import ReportPage from './components/ReportPage';

const App: React.FC = () => {
  const [loading, setLoading] = useState(true);
  const [authorized, setAuthorized] = useState(false);

  useEffect(() => {
    // Проверяем сессию на BFF
    fetch('http://localhost:5000/api/auth/status', { credentials: 'include' })
      .then(res => {
        if (res.ok) setAuthorized(true);
        else window.location.href = 'http://localhost:5000/api/auth/login'; // Редирект на флоу авторизации бэкенда
      })
      .catch(() => window.location.href = 'http://localhost:5000/api/auth/login')
      .finally(() => setLoading(false));
  }, []);

  if (loading) return <div>Инициализация защищенной сессии...</div>;
  if (!authorized) return <div>Перенаправление на авторизацию...</div>;

  return (
    <div className="App">
      {/* Компонент ReportPage внутри себя должен делать fetch на http://localhost:5000/api/proxy/reports с { credentials: 'include' } */}
      <ReportPage />
    </div>
  );
};

export default App;