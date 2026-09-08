# python/__main__.py
import logging
import sys
import asyncio
from typing import Optional

from .config import DatabaseConfig, DispatcherConfig, ProviderConfig, AdapterConfig
from .outbox_dispatcher import OutboxDispatcher
from .receipt_adapter import ReceiptAdapter
from .inbox_reconciler import InboxReconciler

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s - %(name)s - %(levelname)s - %(message)s"
)
logger = logging.getLogger(__name__)


def run_dispatcher() -> None:
    """Entrypoint для outbox-dispatcher."""
    logger.info("Starting outbox-dispatcher")
    
    db_config = DatabaseConfig.from_env("COURSE_OUTBOX")
    dispatcher_config = DispatcherConfig.from_env()
    provider_config = ProviderConfig.from_env()
    
    dispatcher = OutboxDispatcher(db_config, dispatcher_config, provider_config)
    
    try:
        asyncio.run(dispatcher.start())
    except KeyboardInterrupt:
        logger.info("Shutting down...")
        asyncio.run(dispatcher.stop())


def run_adapter() -> None:
    """Entrypoint для receipt-adapter."""
    logger.info("Starting receipt-adapter")
    
    adapter_config = AdapterConfig.from_env()
    adapter = ReceiptAdapter(adapter_config)
    
    try:
        asyncio.run(adapter.start())
        # Keep running
        asyncio.get_event_loop().run_forever()
    except KeyboardInterrupt:
        logger.info("Shutting down...")
        asyncio.run(adapter.stop())


def run_reconciler() -> None:
    """Entrypoint для inbox-reconciler."""
    logger.info("Starting inbox-reconciler")
    
    db_config = DatabaseConfig.from_env("COURSE_INBOX")
    reconciler = InboxReconciler(db_config)
    
    try:
        asyncio.run(reconciler.start())
    except KeyboardInterrupt:
        logger.info("Shutting down...")
        asyncio.run(reconciler.stop())


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python -m python <dispatcher|adapter|reconciler>")
        sys.exit(1)
    
    command = sys.argv[1]
    
    if command == "dispatcher":
        run_dispatcher()
    elif command == "adapter":
        run_adapter()
    elif command == "reconciler":
        run_reconciler()
    else:
        print(f"Unknown command: {command}")
        sys.exit(1)