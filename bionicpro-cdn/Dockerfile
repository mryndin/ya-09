FROM nginx:1.25-alpine

# Удаляем дефолтный конфиг и подкладываем наш кастомный CDN-мидлварь
RUN rm /etc/nginx/conf.d/default.conf
COPY nginx-cdn.conf /etc/nginx/conf.d/cdn.conf

# Создаем директорию под кэш с корректными правами для непривилегированного пользователя nginx
RUN mkdir -p /var/cache/nginx/cdn_zone && chown nginx:nginx /var/cache/nginx/cdn_zone

EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]