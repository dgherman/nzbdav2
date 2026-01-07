import React, { createContext, useContext, useState, useCallback, ReactNode } from 'react';
import { Toast, ToastContainer } from 'react-bootstrap';

export type ToastVariant = 'primary' | 'secondary' | 'success' | 'danger' | 'warning' | 'info' | 'light' | 'dark';

export interface ToastMessage {
    id: number;
    title?: string;
    message: string;
    variant?: ToastVariant;
    duration?: number;
}

interface ToastContextType {
    addToast: (message: string, variant?: ToastVariant, title?: string, duration?: number) => void;
}

const ToastContext = createContext<ToastContextType | undefined>(undefined);

export function useToast() {
    const context = useContext(ToastContext);
    if (!context) {
        throw new Error('useToast must be used within a ToastProvider');
    }
    return context;
}

export function ToastProvider({ children }: { children: ReactNode }) {
    const [toasts, setToasts] = useState<ToastMessage[]>([]);

    const addToast = useCallback((message: string, variant: ToastVariant = 'info', title?: string, duration: number = 5000) => {
        setToasts(prev => [...prev, { id: Date.now() + Math.random(), message, variant, title, duration }]);
    }, []);

    const removeToast = useCallback((id: number) => {
        setToasts(prev => prev.filter(t => t.id !== id));
    }, []);

    return (
        <ToastContext.Provider value={{ addToast }}>
            {children}
            <ToastContainer className="p-3" position="top-end" style={{ zIndex: 9999, position: 'fixed' }}>
                {toasts.map(toast => (
                    <Toast 
                        key={toast.id} 
                        onClose={() => removeToast(toast.id)} 
                        delay={toast.duration} 
                        autohide 
                        bg={toast.variant?.toLowerCase()}
                        text={toast.variant && ['warning', 'info', 'light'].includes(toast.variant) ? 'dark' : 'white'}
                    >
                        {toast.title && <Toast.Header closeButton={true}><strong className="me-auto">{toast.title}</strong></Toast.Header>}
                        <Toast.Body>{toast.message}</Toast.Body>
                    </Toast>
                ))}
            </ToastContainer>
        </ToastContext.Provider>
    );
}
