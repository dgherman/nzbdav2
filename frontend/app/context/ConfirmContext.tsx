import React, { createContext, useContext, useState, useCallback, ReactNode } from 'react';
import { Modal, Button } from 'react-bootstrap';

interface ConfirmOptions {
    title?: string;
    message: string;
    confirmText?: string;
    cancelText?: string;
    variant?: 'primary' | 'danger' | 'warning';
}

interface ConfirmContextType {
    confirm: (options: ConfirmOptions) => Promise<boolean>;
}

const ConfirmContext = createContext<ConfirmContextType | undefined>(undefined);

export function useConfirm() {
    const context = useContext(ConfirmContext);
    if (!context) {
        throw new Error('useConfirm must be used within a ConfirmProvider');
    }
    return context;
}

export function ConfirmProvider({ children }: { children: ReactNode }) {
    const [show, setShow] = useState(false);
    const [options, setOptions] = useState<ConfirmOptions | null>(null);
    const [resolvePromise, setResolvePromise] = useState<((value: boolean) => void) | null>(null);

    const confirm = useCallback((opts: ConfirmOptions): Promise<boolean> => {
        return new Promise((resolve) => {
            setOptions(opts);
            setResolvePromise(() => resolve);
            setShow(true);
        });
    }, []);

    const handleConfirm = useCallback(() => {
        setShow(false);
        resolvePromise?.(true);
        setResolvePromise(null);
        setOptions(null);
    }, [resolvePromise]);

    const handleCancel = useCallback(() => {
        setShow(false);
        resolvePromise?.(false);
        setResolvePromise(null);
        setOptions(null);
    }, [resolvePromise]);

    return (
        <ConfirmContext.Provider value={{ confirm }}>
            {children}
            <Modal show={show} onHide={handleCancel} centered>
                <Modal.Header closeButton>
                    <Modal.Title>{options?.title || 'Confirm'}</Modal.Title>
                </Modal.Header>
                <Modal.Body style={{ whiteSpace: 'pre-wrap' }}>
                    {options?.message}
                </Modal.Body>
                <Modal.Footer>
                    <Button variant="secondary" onClick={handleCancel}>
                        {options?.cancelText || 'Cancel'}
                    </Button>
                    <Button variant={options?.variant || 'primary'} onClick={handleConfirm}>
                        {options?.confirmText || 'Confirm'}
                    </Button>
                </Modal.Footer>
            </Modal>
        </ConfirmContext.Provider>
    );
}
